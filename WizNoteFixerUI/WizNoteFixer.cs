//####################################################################
//	Created:	2011/03/18   11:33
//	Filename: 	WizNoteFixer
//	Author:		viki
//	Email:		windviki@gmail.com
//********************************************************************
//	Comments:	beautify note formats
//
//	UpdateLogs:	
//####################################################################

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;

using WizKMCoreLib;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using System.IO;
using System.Threading;

namespace WizNoteFixerUI
{
    //״̬ö��
    public enum FixerStatus
    {
        None = 0,
        Started = 1,
        PreWork = 2,
        Process = 3,
        Skip = 4,
        Done = 5,
        UserCancel = 6,
        Error = 7,
        FatalEnd = 8,
    }

    //�쳣ö��
    public enum FixerExceptionType
    {
        None = 0,
        OpenDBFailed = 1,
        UpdateDocFailed = 2,
    }

    //�Զ����쳣
    public class FixerException : System.Exception
    {
        private FixerExceptionType m_type;
        private string m_error;// ������Ϣ

        public FixerException()
            : base()
        {
            m_type = FixerExceptionType.None;
        }

        public FixerException(string err, FixerExceptionType fe)
            : base()
        {
            m_error = err;
            m_type = fe;
            switch (fe)
            {
                case FixerExceptionType.OpenDBFailed:
                    m_error += "�����ܴ��û����ݿ⣡";
                    break;
                case FixerExceptionType.UpdateDocFailed:
                    m_error += "�������ĵ�����";
                    break;
            }
        }

        public FixerExceptionType Type
        {
            get { return m_type; }
        }

        public override string ToString()
        {
            return m_error;
        }
    }

    //�¼�����
    public class ProgressEventArgs : EventArgs
    {
        private string _StrInfo;
        public string StrInfo { get { return _StrInfo; } private set { _StrInfo = value; } }
        private FixerStatus _State;
        public FixerStatus State { get { return _State; } private set { _State = value; } }

        public ProgressEventArgs(FixerStatus st, string info)
        {
            this.StrInfo = info;
            this.State = st;
        }
    }

    public abstract class WizThread
    {
        //private Thread m_thread = null;
        private ManualResetEvent m_evstop = new ManualResetEvent(false);

        private WizDatabase _WizDB;
        public WizDatabase DataBase { get { return _WizDB; } set { _WizDB = value; } }

        abstract public void Run();

        public void Start()
        {
            m_evstop.Reset();
            Thread tthread = new Thread(Run);
            tthread.Start();
        }

        protected bool IsNeedStop()
        {
            return m_evstop.WaitOne(0);
        }

        public void Stop()
        {
            m_evstop.Set();
        }
    }

    public class ParameterList
    {
        private String _Raw = "";
        public String Raw { get { return _Raw; } set { _Raw = value; } }
        private Dictionary<String, String> _Dict = new Dictionary<String, String>();
        public Dictionary<String, String> Dict { get { return _Dict; } private set { _Dict = value; } }

        public ParameterList()
        {

        }

        public ParameterList(String strraw)
        {
            _Raw = strraw;
        }

        public void Parse()
        {
            char[] chsplit1 = { ';' }; // outter layer
            char[] chsplit2 = { '=', ':' }; // inner layer
            String[] strarray1 = _Raw.Trim().Split(chsplit1);
            foreach (String sitem in strarray1)
            {
                if (sitem.Length > 0)
                {
                    String[] strarray2 = sitem.Trim().Split(chsplit2);
                    if (strarray2.Length == 1)
                    {
                        _Dict[strarray2[0].Trim()] = "";
                    }
                    if (strarray2.Length == 2)
                    {
                        _Dict[strarray2[0].Trim()] = strarray2[1].Trim();
                    }
                }
            }
        }

        public void Parse(String strraw)
        {
            _Raw = strraw;
            Parse();
        }

        public String Value()
        {
            _Raw = "";
            foreach (KeyValuePair<String, String> kv in _Dict)
            {
                _Raw += String.Format("{0}:{1};", kv.Key, kv.Value);
            }
            return _Raw;
        }
    }

    public class WalkNotesThread : WizThread
    {
        private IWizDocumentCollection _Docs;
        public IWizDocumentCollection Docs { get { return _Docs; } set { _Docs = value; } }

        private bool _FixImg = false;
        public bool FixImg { get { return _FixImg; } set { _FixImg = value; } }

        private bool _ReplaceFont = false;
        public bool ReplaceFont { get { return _ReplaceFont; } set { _ReplaceFont = value; } }

        private bool _FixOneNoteDIV = false;
        public bool FixOneNoteDIV { get { return _FixOneNoteDIV; } set { _FixOneNoteDIV = value; } }

        private Dictionary<String, String> _Fonts = new Dictionary<String, String>();
        public Dictionary<String, String> Fonts { get { return _Fonts; } set { _Fonts = value; } }

        private int _NotesCount = 0;
        public int NotesCount { get { return _NotesCount; } private set { _NotesCount = value; } }

        private int _ProcessCount = 0;
        public int ProcessCount { get { return _ProcessCount; } private set { _ProcessCount = value; } }

        //�¼�����
        public delegate void ProgressHandler(object sender, ProgressEventArgs e);
        //�¼�
        public event ProgressHandler OnProgress = null;

        override public void Run()
        {
            _ProcessCount = 0;
            _NotesCount = Docs.Count;
            OnProgress(this, new ProgressEventArgs(FixerStatus.Started, "��ʼ�޸���"));

            char[] chend = { '\n', '\r' };
            HtmlAgilityPack.HtmlDocument hdoc = new HtmlAgilityPack.HtmlDocument();
            bool bUserCancel = false;
            bool bNeedUpdate = false;

            //������ʱ�ļ���
            string strtempfolderpath = Path.Combine(Path.GetTempPath(), "WizNoteFixer");
            if (!Directory.Exists(strtempfolderpath))
            {
                Directory.CreateDirectory(strtempfolderpath);
            }
            //��ʱ�ļ�
            string strtemphtml = Path.Combine(strtempfolderpath, "temp.html");

            DataBase.EnableDocumentIndexing(false);
            DataBase.BeginUpdate();

            foreach (IWizDocument wdoc in Docs)
            {
                bNeedUpdate = false;
                //ֹͣ
                if (IsNeedStop())
                {
                    bUserCancel = true;
                    break;
                }

                _ProcessCount++;
                strtemphtml = Path.Combine(strtempfolderpath, wdoc.GUID + ".html");

                //string strhtml;
                try
                {
                    wdoc.SaveToHtml(strtemphtml, 1);
                    //strhtml = wdoc.GetHtml().TrimEnd(chend);
                }
                catch (System.Exception ex)
                {
                    OnProgress(this, new ProgressEventArgs(FixerStatus.Error, String.Format("��ȡ{0}���ݳ���{1}", wdoc.FileName, ex.ToString())));
                    continue;
                }

                //hdoc.LoadHtml(strhtml);
                hdoc.Load(strtemphtml);
                if (_FixImg)
                {
                    HtmlAgilityPack.HtmlNodeCollection imgnodes = hdoc.DocumentNode.SelectNodes("//img");
                    if (imgnodes != null && imgnodes.Count > 0)
                    {
                        //OnProgress(this, new ProgressEventArgs(FixerStatus.Working, "�����ĵ�img��ǩ��" + wdoc.FileName));

                        foreach (HtmlNode img in imgnodes)
                        {
                            if (img.Attributes.Contains("style")) //����style����
                            {
                                HtmlAttribute attr = img.Attributes["style"];
                                //ע��xxxheight, xxx-height֮���style������ɾ
                                ParameterList plattr = new ParameterList(attr.Value.ToLower());
                                plattr.Parse();
                                if (plattr.Dict.ContainsKey("height") || plattr.Dict.ContainsKey("width"))
                                {
                                    //ɾ��style�е�height
                                    plattr.Dict.Remove("height");
                                    //ɾ��style�е�width
                                    plattr.Dict.Remove("width");
                                    attr.Value = plattr.Value();
                                    bNeedUpdate = true;
                                }
                            }
                            //����height,width����
                            else if (img.Attributes.Contains("height"))
                            {
                                img.Attributes["height"].Remove();
                                bNeedUpdate = true;
                            }
                            else if (img.Attributes.Contains("width"))
                            {
                                img.Attributes["width"].Remove();
                                bNeedUpdate = true;
                            }
                        }
                    }
                }
                if (_ReplaceFont)
                {
                    HtmlAgilityPack.HtmlNodeCollection stylenodes = hdoc.DocumentNode.SelectNodes("//p[@style] | //font[@face] | //span[@style]");
                    if (stylenodes != null && stylenodes.Count > 0)
                    {
                        //OnProgress(this, new ProgressEventArgs(FixerStatus.Working, "�����ĵ�p��ǩ��" + wdoc.FileName));

                        foreach (HtmlNode stylenode in stylenodes)
                        {
                            foreach (HtmlAttribute attr in stylenode.Attributes)
                            {
                                if (attr.Name == "style" || attr.Name == "face")
                                {
                                    foreach (string oldfont in Fonts.Keys)
                                    {
                                        if (attr.Value.IndexOf(oldfont) >= 0)
                                        {
                                            attr.Value = attr.Value.Replace(oldfont, Fonts[oldfont]);
                                            bNeedUpdate = true;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                if (_FixOneNoteDIV)
                {
                    bool bNeedFixDiv = false;
                    HtmlAgilityPack.HtmlNodeCollection metanodes = hdoc.DocumentNode.SelectNodes("/html/head/meta");
                    if (metanodes != null && metanodes.Count > 0)
                    {
                        String strProgId = "";
                        ParameterList plWizNoteFixerTag = new ParameterList();

                        bool bHasWizNoteFixerTag = false;
                        foreach (HtmlNode metanode in metanodes)
                        {
                            HtmlAgilityPack.HtmlAttributeCollection metaatts = metanode.Attributes;
                            if (metaatts.Contains("name") && metaatts.Contains("content"))
                            {
                                if (metaatts["name"].Value == "ProgId")
                                {
                                    strProgId = metaatts["content"].Value;
                                }
                                if (metaatts["name"].Value == "WizNoteFixerTag")
                                {
                                    bHasWizNoteFixerTag = true;
                                    plWizNoteFixerTag.Parse(metaatts["content"].Value);
                                }
                            }
                        }

                        //����ǲ���OneNote�ʼ��ļ�
                        if (strProgId == "OneNote.File")
                        {
                            bNeedFixDiv = true;
                            if (plWizNoteFixerTag.Dict.ContainsKey("div"))
                            {
                                if (plWizNoteFixerTag.Dict["div"] == "true")
                                {
                                    //�Ѿ�������ˣ�����
                                    bNeedFixDiv = false;
                                    OnProgress(this, new ProgressEventArgs(FixerStatus.Skip, String.Format("�Ѿ��޸���DIV�ڵ㣬����{0}", wdoc.FileName)));
                                }
                            }
                            //����DIV������ϵı�ʶ
                            if (bNeedFixDiv)
                            {
                                plWizNoteFixerTag.Dict["div"] = "true";

                                //���ӱ������ meta TAG
                                if (!bHasWizNoteFixerTag)
                                {
                                    HtmlNode metatagnode = metanodes[0].Clone();
                                    metatagnode.Attributes.RemoveAll();
                                    metatagnode.Attributes.Add("name", "WizNoteFixerTag");
                                    metatagnode.Attributes.Add("content", "");
                                    hdoc.DocumentNode.SelectSingleNode("/html/head").InsertAfter(metatagnode, metanodes[metanodes.Count - 1]);
                                }

                                hdoc.DocumentNode.SelectSingleNode("/html/head/meta[@name=\"WizNoteFixerTag\"]").Attributes["content"].Value = plWizNoteFixerTag.Value();
                            }
                        }
                    }

                    HtmlAgilityPack.HtmlNode divnode = hdoc.DocumentNode.SelectSingleNode("/html/body/div/div");
                    if (bNeedFixDiv && divnode != null && divnode.ChildNodes.Count >= 5)
                    {
                        int nDivCount = 0;
                        foreach (HtmlNode childnode in divnode.ChildNodes)
                        {
                            if (childnode.Name == "div")
                            {
                                nDivCount++;
                            }
                            if (nDivCount == 3)
                            {
                                //divnode.ChildNodes[1] //title
                                //divnode.ChildNodes[3] //date time
                                //divnode.ChildNodes[5] //content
                                HtmlAgilityPack.HtmlAttributeCollection divatts = childnode.Attributes;
                                if (divatts.Contains("style"))
                                {
                                    divatts["style"].Value = Regex.Replace(divatts["style"].Value, "width:[^;]*", "width:100%");
                                }
                                divnode.ParentNode.AppendChildren(divnode.ChildNodes);
                                divnode.Remove();
                                bNeedUpdate = true;
                                break;
                            }
                        }
                    }
                }

                if (bNeedUpdate)
                {
                    hdoc.Save(strtemphtml);

                    OnProgress(this, new ProgressEventArgs(FixerStatus.Process,
                        String.Format("�����ĵ�{0}/{1}��{2}{3}", _ProcessCount, _NotesCount, wdoc.Location, wdoc.Name)));

                    //�����ĵ����ݣ�ͨ��һ��html�ļ��������ĵ���
                    //wizUpdateDocumentSaveSel = 0��0001    ����ѡ�в��֣��������UpdateDocument2��Ч
                    //wizUpdateDocumentIncludeScript = 0��0002    ����html����Ľű�
                    //wizUpdateDocumentShowProgress = 0��0004    ��ʾ����
                    //wizUpdateDocumentSaveContentOnly = 0��0008   ֻ�������� 
                    //wizUpdateDocumentSaveTextOnly = 0��0010    ֻ�����������ݣ�����Ϊ���ı�
                    //wizUpdateDocumentDonotDownloadFile = 0��0020    ������������html�������Դ
                    //wizUpdateDocumentAllowAutoGetContent = 0��0040    ���ֻ�������ģ�����ʹ���Զ�������ķ�ʽ

                    wdoc.UpdateDocument(strtemphtml, 2);
                    //wdoc.UpdateDocument3(hdoc.DocumentNode.OuterHtml, 64);
                    //break;
                }
                else
                {
                    OnProgress(this, new ProgressEventArgs(FixerStatus.Skip,
                        String.Format("�����ĵ�{0}/{1}��{2}{3}", _ProcessCount, _NotesCount, wdoc.Location, wdoc.Name)));
                }
            }

            DataBase.EndUpdate();
            DataBase.EnableDocumentIndexing(true);
            //DataBase.Close();

            Directory.Delete(strtempfolderpath, true);

            if (bUserCancel)
            {
                OnProgress(this, new ProgressEventArgs(FixerStatus.Done,
                    String.Format("�û�ȡ�����Ѿ�����{0}/{1}", _ProcessCount, _NotesCount)));
            }
            else
            {
                OnProgress(this, new ProgressEventArgs(FixerStatus.Done, "ȫ���ʼǴ��������"));
            }
        }
    }


    public class BackupDBThread : WizThread
    {
        //�¼�����
        public delegate void ProgressHandler(object sender, ProgressEventArgs e);
        //�¼�
        public event ProgressHandler OnProgress = null;

        override public void Run()
        {
            OnProgress(this, new ProgressEventArgs(FixerStatus.Started, "���û����ݿ�"));

            ////��ʱ��֪���û�ѡ���ı���·����ʲô�ֶΣ�ûд�����ã�
            ////WizSettings wst = new WizSettingsClass();
            ////string strbackuppath = wst.get_StringValue("", "");

            string strbackuppath = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            strbackuppath = Path.Combine(strbackuppath, "WizDataBaseBackup");
            if (!Directory.Exists(strbackuppath))
            {
                Directory.CreateDirectory(strbackuppath);
            }
            strbackuppath = Path.Combine(strbackuppath, "WizDB-ImgFixer-" + DateTime.Now.ToString("yyyyMMddhhmmss"));

            //wdb.BackupIndex();
            DataBase.BackupAll(strbackuppath);

            //Thread.Sleep(3000);
            OnProgress(this, new ProgressEventArgs(FixerStatus.Done, "���ݳɹ���" + strbackuppath));
        }
    }
}