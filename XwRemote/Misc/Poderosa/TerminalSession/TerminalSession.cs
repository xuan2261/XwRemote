/*
 * Copyright 2004,2006 The Poderosa Project.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 *
 * $Id: TerminalSession.cs,v 1.4 2012/02/19 09:11:28 kzmi Exp $
 */
using System;
using System.Windows.Forms;
using System.Collections;
using System.Drawing;
using System.Diagnostics;

using Poderosa.Document;
using Poderosa.Terminal;
using Poderosa.View;
using Poderosa.Protocols;
using Poderosa.ConnectionParam;
using Poderosa.Forms;
using Poderosa.Util;

namespace Poderosa.Sessions
{
    //NOTE Invalidate�ɕK�v�ȃp�����[�^ ������Ӑ}�����܂������Ȃ�
    internal class InvalidateParam
    {
        private Delegate _delegate;
        private object[] _param;
        private bool _set;
        public void Set(Delegate d, object[] p)
        {
            _delegate = d;
            _param = p;
            _set = true;
        }
        public void Reset()
        {
            _set = false;
        }
        public void InvokeFor(Control c)
        {
            if (_set)
                c.Invoke(_delegate, _param);
        }
    }

    //�ڑ��ɑ΂��Ċ֘A�t����f�[�^
    public class TerminalSession : ITerminalSession, IAbstractTerminalHost, ITerminalControlHost
    {
        private delegate void HostCauseCloseDelagate(string msg);

        private ISessionHost _sessionHost;
        private TerminalTransmission _output;
        private AbstractTerminal _terminal;
        private ITerminalSettings _terminalSettings;
        private Poderosa.Terminal.TerminalControl _terminalControl;
        private bool _terminated;

        public TerminalSession(ITerminalConnection connection, ITerminalSettings terminalSettings)
        {
            _terminalSettings = terminalSettings;
            //VT100�w��ł�xterm�V�[�P���X�𑗂��Ă���A�v���P�[�V��������������Ȃ��̂�
            _terminal = AbstractTerminal.Create(new TerminalInitializeInfo(this, connection.Destination));
            _output = new TerminalTransmission(_terminal, _terminalSettings, connection);

            _terminalSettings.ChangeCaption += delegate (string caption) {
                this.OwnerWindow.DocumentTabFeature.Update(_terminal.IDocument);
            };

        }

        public void Revive(ITerminalConnection connection)
        {
            TerminalDocument doc = _terminal.GetDocument();
            _output.Revive(connection, doc.TerminalWidth, doc.TerminalHeight);
            this.OwnerWindow.DocumentTabFeature.Update(_terminal.IDocument);
            _output.Connection.Socket.RepeatAsyncRead(_terminal); //�Ď�M
        }

        //IAdaptable
        public IAdaptable GetAdapter(Type adapter)
        {
            return TerminalSessionsPlugin.Instance.PoderosaWorld.AdapterManager.GetAdapter(this, adapter);
        }

        #region ITerminalSession
        public AbstractTerminal Terminal
        {
            get
            {
                return _terminal;
            }
        }
        public Poderosa.Terminal.TerminalControl TerminalControl
        {
            get
            {
                return _terminalControl;
            }
        }
        public IPoderosaMainWindow OwnerWindow
        {
            get
            {
                if (_terminated)
                    return TerminalSessionsPlugin.Instance.WindowManager.ActiveWindow; //�I�����Ă���Ƃ���SessionHost�����擾�s�\
                else
                    return (IPoderosaMainWindow)_sessionHost.GetParentFormFor(_terminal.IDocument).GetAdapter(typeof(IPoderosaMainWindow));
            }
        }
        public ITerminalConnection TerminalConnection
        {
            get
            {
                return _output.Connection;
            }
        }
        public ITerminalSettings TerminalSettings
        {
            get
            {
                return _terminalSettings;
            }
        }
        public TerminalTransmission TerminalTransmission
        {
            get
            {
                return _output;
            }
        }
        public ISession ISession
        {
            get
            {
                return this;
            }
        }
        /*
        public ILogService LogService {
            get {
                return _terminal.ILogService;
            }
        }*/
        #endregion

        //��M�X���b�h����ĂԁADocument�X�V�̒ʒm
        public void NotifyViewsDataArrived()
        {
            if (_terminalControl != null)
                _terminalControl.DataArrived();
        }
        //����E�ُ�Ƃ��Ă΂��
        public void CloseByReceptionThread(string msg)
        {
            if (_terminated)
                return;
            IPoderosaMainWindow window = this.OwnerWindow;
            if (window != null)
            {
                Debug.Assert(window.AsControl().InvokeRequired);
                //TerminalSession�̓R���g���[����ۗL���Ȃ��̂ŁA�E�B���h�E�ő�p����
                window.AsControl().Invoke(new HostCauseCloseDelagate(HostCauseClose), msg);
            }
        }
        private void HostCauseClose(string msg)
        {
            if (TerminalSessionsPlugin.Instance.TerminalEmulatorService.TerminalEmulatorOptions.CloseOnDisconnect)
                _sessionHost.TerminateSession();
            else
            {
                IPoderosaMainWindow window = this.OwnerWindow;
                window.DocumentTabFeature.Update(_terminal.IDocument);
            }
        }

        //ISession
        public string Caption
        {
            get
            {
                string s = _terminalSettings.Caption;
                if (_output.Connection.IsClosed)
                    s += TEnv.Strings.GetString("Caption.Disconnected");
                return s;
            }
        }
        public Image Icon
        {
            get
            {
                return _terminalSettings.Icon;
            }
        }
        //TerminalSession�̊J�n
        public void InternalStart(ISessionHost host)
        {
            _sessionHost = host;
            host.RegisterDocument(_terminal.IDocument);
            _output.Connection.Socket.RepeatAsyncRead(_terminal);
        }
        public void InternalTerminate()
        {
            _terminated = true;
            try
            {
                _output.Connection.Close();
                _output.Connection.Socket.ForceDisposed();
            }
            catch (Exception)
            { //�����ł̗�O�͖���
            }
            _terminal.CloseBySession();
        }
        public PrepareCloseResult PrepareCloseDocument(IPoderosaDocument document)
        {
            Debug.Assert(document == _terminal.IDocument);
            return PrepareCloseResult.TerminateSession;
        }
        public PrepareCloseResult PrepareCloseSession()
        {
            if (TerminalSessionsPlugin.Instance.TerminalSessionOptions.AskCloseOnExit && !_output.Connection.IsClosed)
            {
                if (this.OwnerWindow.AskUserYesNo(String.Format(TEnv.Strings.GetString("Message.AskCloseTerminalSession"), this.Caption)) == DialogResult.Yes)
                    return PrepareCloseResult.TerminateSession;
                else
                    return PrepareCloseResult.Cancel;
            }
            else
                return PrepareCloseResult.TerminateSession;
        }

        public void InternalAttachView(IPoderosaDocument document, IPoderosaView view)
        {
            Debug.WriteLineIf(DebugOpt.ViewManagement, "ATTACH VIEW");
            Debug.Assert(document == _terminal.IDocument);
            TerminalView tv = (TerminalView)view.GetAdapter(typeof(TerminalView));
            Debug.Assert(tv != null);
            Poderosa.Terminal.TerminalControl tp = tv.TerminalControl;
            Debug.Assert(tp != null);
            tp.Attach(this);

            _terminalControl = tp;
            _terminal.Attached(tp);
        }
        public void InternalDetachView(IPoderosaDocument document, IPoderosaView view)
        {
            Debug.WriteLineIf(DebugOpt.ViewManagement, "DETACH VIEW");
            Debug.Assert(document == _terminal.IDocument);
            TerminalView tv = (TerminalView)view.GetAdapter(typeof(TerminalView));
            Debug.Assert(tv != null);
            Poderosa.Terminal.TerminalControl tp = tv.TerminalControl;
            Debug.Assert(tp != null); //Detach����Ƃ��ɂ͂��̃r���[�ɂȂ��Ă���K�v����

            if (!tp.IsDisposed)
            {
                _terminal.Detach(tp);
                tp.Detach();
            }

            _terminalControl = null;
        }
        public void InternalCloseDocument(IPoderosaDocument document)
        {
            //do nothing
        }
#if false //�p�~
        //�r���[����̃R���g���[���̎擾
        private static TerminalControl CastTerminalControl(IPoderosaView view) {
            IContentReplaceableView rv = (IContentReplaceableView)view.GetAdapter(typeof(IContentReplaceableView));
            Debug.Assert(rv!=null); //����ł͕��������ł������삵�Ă��Ȃ��̂ł����܂ł͕K������
            IPoderosaView content = rv.GetCurrentContent();

            if(content is TerminalView)
                return ((TerminalView)content).TerminalControl;
            else
                return null;
        }
        private static TerminalControl CastOrCreateTerminalControl(IPoderosaView view) {
            TerminalControl c = CastTerminalControl(view);
            if(c!=null) return c; //�L���X�g�ł���΂����OK�B�łȂ���΍��

            Debug.WriteLine("Creating New TerminalControl");
            IContentReplaceableView rv = (IContentReplaceableView)view.GetAdapter(typeof(IContentReplaceableView));
            TerminalControl tc = new TerminalControl();
            rv.ReplaceContent(new TerminalView(view.ParentForm, tc));
            return tc;
        }
#endif
    }

}