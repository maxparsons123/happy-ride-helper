using System;
using System.Runtime.InteropServices;
using VaxServerVoAILib;

public class cVaxServerCOM
{
    public const int SIP_DEFAULT_PORT = 5060;
       
    public const int VAX_PEER_TYPE_USER = 0;
    public const int VAX_PEER_TYPE_LINE = 1;

    public const int VAX_LINE_TYPE_UDP = 0;
    public const int VAX_LINE_TYPE_TCP = 1;
    public const int VAX_LINE_TYPE_TLS = 2;

    public const int VAX_REC_MUTE_TYPE_NONE = -1;
    public const int VAX_REC_MUTE_TYPE_SPEAK = 0;
    public const int VAX_REC_MUTE_TYPE_LISTEN = 1;
    public const int VAX_REC_MUTE_TYPE_BOTH = 2;

    ///////////////////////////////////////////////////////////

    public const int VAX_WAVE_REC_TYPE_MONO_PCM = 0;
    public const int VAX_WAVE_REC_TYPE_STEREO_PCM = 1;
    public const int VAX_WAVE_REC_TYPE_MONO_G711U = 2;
    public const int VAX_WAVE_REC_TYPE_STEREO_G711U = 3;
    public const int VAX_WAVE_REC_TYPE_MONO_G711A = 4;
    public const int VAX_WAVE_REC_TYPE_STEREO_G711A = 5;
        
    ///////////////////////////////////////////////////////////

    public const int VAX_DTMF_TYPE_RFC2833 = 0;
    public const int VAX_DTMF_TYPE_SIP_INFO = 1;
    public const int VAX_DTMF_TYPE_INBAND = 2;

    public const int VAX_PLAY_WAVE_MODE_EXCLUSIVE = 0;
    public const int VAX_PLAY_WAVE_MODE_MIXED = 1;
    
    public const int VAX_CODEC_G711A = 2;
    public const int VAX_CODEC_G711U = 3;
    
    public const int VAX_CODEC_TOTAL_COUNT = 2;

    ///////////////////////////////////////////////////////////

    public const int VAX_CUSTOM_HEADER_REQ_ID_INVITE = 0;

    ///////////////////////////////////////////////////////////

    public const int VAX_CLOSED_REASON_CODE_HANGUP = 0;
    public const int VAX_CLOSED_REASON_CODE_SESSION_LOST = 1;
    public const int VAX_CLOSED_REASON_CODE_MOVED = 2;
    public const int VAX_CLOSED_REASON_CODE_TRANSFERED = 3;
    public const int VAX_CLOSED_REASON_CODE_REJECTED = 4;
    public const int VAX_CLOSED_REASON_CODE_FAILED = 5;
    public const int VAX_CLOSED_REASON_CODE_CANCELLED = 6;
    public const int VAX_CLOSED_REASON_CODE_TIMEOUT = 7;
    public const int VAX_CLOSED_REASON_CODE_CLOSED = 8;
    public const int VAX_CLOSED_REASON_CODE_SEND_REDIRECT_RESPONSE = 9;
    public const int VAX_CLOSED_REASON_CODE_SEND_FAILURE_RESPONSE = 10;

    ///////////////////////////////////////////////////////////

    private ServerLibVoAI m_objServerVoAI = new ServerLibVoAI();

    public cVaxServerCOM()
    {

        m_objServerVoAI.OnRegisterUser += new _IServerLibVoAIEvents_OnRegisterUserEventHandler(OnRegisterUser);
        m_objServerVoAI.OnRegisterUserSuccess += new _IServerLibVoAIEvents_OnRegisterUserSuccessEventHandler(OnRegisterUserSuccess);
        m_objServerVoAI.OnRegisterUserFailed += new _IServerLibVoAIEvents_OnRegisterUserFailedEventHandler(OnRegisterUserFailed);
        m_objServerVoAI.OnUnRegisterUser += new _IServerLibVoAIEvents_OnUnRegisterUserEventHandler(OnUnRegisterUser);

        m_objServerVoAI.OnCallSessionCreated += new _IServerLibVoAIEvents_OnCallSessionCreatedEventHandler(OnCallSessionCreated);
        m_objServerVoAI.OnCallSessionClosed += new _IServerLibVoAIEvents_OnCallSessionClosedEventHandler(OnCallSessionClosed);

        m_objServerVoAI.OnCallSessionConnecting += new _IServerLibVoAIEvents_OnCallSessionConnectingEventHandler(OnCallSessionConnecting);
        m_objServerVoAI.OnCallSessionFailed += new _IServerLibVoAIEvents_OnCallSessionFailedEventHandler(OnCallSessionFailed);
        m_objServerVoAI.OnCallSessionConnected += new _IServerLibVoAIEvents_OnCallSessionConnectedEventHandler(OnCallSessionConnected);

        m_objServerVoAI.OnIncomingCall += new _IServerLibVoAIEvents_OnIncomingCallEventHandler(OnIncomingCall);

        m_objServerVoAI.OnCallSessionLost += new _IServerLibVoAIEvents_OnCallSessionLostEventHandler(OnCallSessionLost);
        m_objServerVoAI.OnCallSessionHangup += new _IServerLibVoAIEvents_OnCallSessionHangupEventHandler(OnCallSessionHangup);
        m_objServerVoAI.OnCallSessionTimeout += new _IServerLibVoAIEvents_OnCallSessionTimeoutEventHandler(OnCallSessionTimeout);
        m_objServerVoAI.OnCallSessionCancelled += new _IServerLibVoAIEvents_OnCallSessionCancelledEventHandler(OnCallSessionCancelled);

        m_objServerVoAI.OnChatMessageText += new _IServerLibVoAIEvents_OnChatMessageTextEventHandler(OnChatMessageText);
        m_objServerVoAI.OnChatMessageSuccess += new _IServerLibVoAIEvents_OnChatMessageSuccessEventHandler(OnChatMessageSuccess);
        m_objServerVoAI.OnChatMessageFailed += new _IServerLibVoAIEvents_OnChatMessageFailedEventHandler(OnChatMessageFailed);
        m_objServerVoAI.OnChatMessageTimeout += new _IServerLibVoAIEvents_OnChatMessageTimeoutEventHandler(OnChatMessageTimeout);

        m_objServerVoAI.OnVaxFunctionCallOpenAI += new _IServerLibVoAIEvents_OnVaxFunctionCallOpenAIEventHandler(OnVaxFunctionCallOpenAI);
        m_objServerVoAI.OnVaxSessionUpdatedOpenAI += new _IServerLibVoAIEvents_OnVaxSessionUpdatedOpenAIEventHandler(OnVaxSessionUpdatedOpenAI);
        
        m_objServerVoAI.OnVaxAudioOutputTranscriptOpenAI += new _IServerLibVoAIEvents_OnVaxAudioOutputTranscriptOpenAIEventHandler(OnVaxAudioOutputTranscriptOpenAI);
        m_objServerVoAI.OnVaxAudioInputTranscriptOpenAI += new _IServerLibVoAIEvents_OnVaxAudioInputTranscriptOpenAIEventHandler(OnVaxAudioInputTranscriptOpenAI);

        m_objServerVoAI.OnVaxResponseDoneUsageOpenAI += new _IServerLibVoAIEvents_OnVaxResponseDoneUsageOpenAIEventHandler(OnVaxResponseDoneUsageOpenAI);
        m_objServerVoAI.OnVaxStatusOpenAI += new _IServerLibVoAIEvents_OnVaxStatusOpenAIEventHandler(OnVaxStatusOpenAI);
        m_objServerVoAI.OnVaxErrorOpenAI += new _IServerLibVoAIEvents_OnVaxErrorOpenAIEventHandler(OnVaxErrorOpenAI);

        m_objServerVoAI.OnLineRegisterTrying += new _IServerLibVoAIEvents_OnLineRegisterTryingEventHandler(OnLineRegisterTrying);
        m_objServerVoAI.OnLineRegisterFailed += new _IServerLibVoAIEvents_OnLineRegisterFailedEventHandler(OnLineRegisterFailed);
        m_objServerVoAI.OnLineRegisterSuccess += new _IServerLibVoAIEvents_OnLineRegisterSuccessEventHandler(OnLineRegisterSuccess);
        m_objServerVoAI.OnLineUnRegisterTrying += new _IServerLibVoAIEvents_OnLineUnRegisterTryingEventHandler(OnLineUnRegisterTrying);
        m_objServerVoAI.OnLineUnRegisterFailed += new _IServerLibVoAIEvents_OnLineUnRegisterFailedEventHandler(OnLineUnRegisterFailed);
        m_objServerVoAI.OnLineUnRegisterSuccess += new _IServerLibVoAIEvents_OnLineUnRegisterSuccessEventHandler(OnLineUnRegisterSuccess);

        m_objServerVoAI.OnAttackDetectedScanSIP += new _IServerLibVoAIEvents_OnAttackDetectedScanSIPEventHandler(OnAttackDetectedScanSIP);
        m_objServerVoAI.OnAttackDetectedFloodSIP += new _IServerLibVoAIEvents_OnAttackDetectedFloodSIPEventHandler(OnAttackDetectedFloodSIP);
        m_objServerVoAI.OnAttackDetectedBruteForceSIP += new _IServerLibVoAIEvents_OnAttackDetectedBruteForceSIPEventHandler(OnAttackDetectedBruteForceSIP);

        m_objServerVoAI.OnSendReqTransferCallTimeout += new _IServerLibVoAIEvents_OnSendReqTransferCallTimeoutEventHandler(OnSendReqTransferCallTimeout);
        m_objServerVoAI.OnSendReqTransferCallAccepted += new _IServerLibVoAIEvents_OnSendReqTransferCallAcceptedEventHandler(OnSendReqTransferCallAccepted);
        m_objServerVoAI.OnSendReqTransferCallFailed += new _IServerLibVoAIEvents_OnSendReqTransferCallFailedEventHandler(OnSendReqTransferCallFailed);

        m_objServerVoAI.OnServerConnectingREC += new _IServerLibVoAIEvents_OnServerConnectingRECEventHandler(OnServerConnectingREC);
        m_objServerVoAI.OnServerConnectedREC += new _IServerLibVoAIEvents_OnServerConnectedRECEventHandler(OnServerConnectedREC);
        m_objServerVoAI.OnServerFailedREC += new _IServerLibVoAIEvents_OnServerFailedRECEventHandler(OnServerFailedREC);
        m_objServerVoAI.OnServerTimeoutREC += new _IServerLibVoAIEvents_OnServerTimeoutRECEventHandler(OnServerTimeoutREC);
        m_objServerVoAI.OnServerHungupREC += new _IServerLibVoAIEvents_OnServerHungupRECEventHandler(OnServerHungupREC);

        m_objServerVoAI.OnVaxVectorSearchStartedOpenAI += new _IServerLibVoAIEvents_OnVaxVectorSearchStartedOpenAIEventHandler(OnVaxVectorSearchStartedOpenAI);
        m_objServerVoAI.OnVaxVectorSearchTryingOpenAI += new _IServerLibVoAIEvents_OnVaxVectorSearchTryingOpenAIEventHandler(OnVaxVectorSearchTryingOpenAI);
        m_objServerVoAI.OnVaxVectorSearchSuccessOpenAI += new _IServerLibVoAIEvents_OnVaxVectorSearchSuccessOpenAIEventHandler(OnVaxVectorSearchSuccessOpenAI);
        m_objServerVoAI.OnVaxVectorSearchFailedOpenAI += new _IServerLibVoAIEvents_OnVaxVectorSearchFailedOpenAIEventHandler(OnVaxVectorSearchFailedOpenAI);

        m_objServerVoAI.OnCallSessionErrorLog += new _IServerLibVoAIEvents_OnCallSessionErrorLogEventHandler(OnCallSessionErrorLog);
        m_objServerVoAI.OnVaxErrorLog += new _IServerLibVoAIEvents_OnVaxErrorLogEventHandler(OnVaxErrorLog);

    }

    #region Methods

    public int GetVaxErrorCode()
    {
        return m_objServerVoAI.GetVaxErrorCode();
    }

    public String GetVaxErrorText()
    {
        return m_objServerVoAI.GetVaxErrorText();
    }

    public String GetVersionFile()
    {
        return m_objServerVoAI.GetVersionFile();
    }

    public String GetVersionSDK()
    {
        return m_objServerVoAI.GetVersionSDK();
    }

    public void SetLicenseKey(String sLicenseKey)
    {
        m_objServerVoAI.SetLicenseKey(sLicenseKey);
    }

    public Boolean Initialize(String sDomainRealm)
    {
        return m_objServerVoAI.Initialize(sDomainRealm);
    }

    public void UnInitialize()
    {
        m_objServerVoAI.UnInitialize();
    }

    public Boolean SetListenPortRangeRTP(int nListenStartPort, int nListenEndPort)
    {
        return m_objServerVoAI.SetListenPortRangeRTP(nListenStartPort, nListenEndPort);
    }

    public Boolean SetNetworkMediaRTP(String sListenIP, int nListenStartPort)
    {
        return m_objServerVoAI.SetNetworkMediaRTP(sListenIP, nListenStartPort);
    }

    public Boolean CryptoMediaNONE(Boolean bForced)
    {
        return m_objServerVoAI.CryptoMediaNONE(bForced);
    }

    public Boolean CryptoMediaSDP(Boolean bForced)
    {
        return m_objServerVoAI.CryptoMediaSDP(bForced);
    }

    public Boolean AddNetworkRouteSIP(String sAssignedIP, String sRouterIP)
    {
        return m_objServerVoAI.AddNetworkRouteSIP(sAssignedIP, sRouterIP);
    }

    public Boolean AddNetworkRouteRTP(String sAssignedIP, String sRouterIP)
    {
        return m_objServerVoAI.AddNetworkRouteRTP(sAssignedIP, sRouterIP);
    }

    public Boolean OpenNetworkUDP(String sListenIP, int nListenPort)
    {
        return m_objServerVoAI.OpenNetworkUDP(sListenIP, nListenPort);
    }

    public Boolean OpenNetworkTCP(String sListenIP, int nListenPort)
    {
        return m_objServerVoAI.OpenNetworkTCP(sListenIP, nListenPort);
    }

    public Boolean OpenNetworkTLS(String sListenIP, int nListenPort, String sCertPEM)
    {
        return m_objServerVoAI.OpenNetworkTLS(sListenIP, nListenPort, sCertPEM);
    }

    public void CloseNetworkUDP()
    {
        m_objServerVoAI.CloseNetworkUDP();
    }

    public void CloseNetworkTCP()
    {
        m_objServerVoAI.CloseNetworkTCP();
    }

    public void CloseNetworkTLS()
    {
        m_objServerVoAI.CloseNetworkTLS();
    }

    public Boolean AddUser(String sUserName, String sPassword, String sAudioCodecList)
    {
        return m_objServerVoAI.AddUser(sUserName, sPassword, sAudioCodecList);
    }

    public void RemoveUser(String sUserName)
    {
        m_objServerVoAI.RemoveUser(sUserName);
    }

    public Boolean RegisterUserExpiry(int nExpiry)
    {
        return m_objServerVoAI.RegisterUserExpiry(nExpiry);
    }

    public Boolean AttachRegister(ulong nRegId, String sUserName)
    {
        return m_objServerVoAI.AttachRegister(nRegId, sUserName);
    }

    public Boolean AcceptRegister(ulong nRegId)
    {
        return m_objServerVoAI.AcceptRegister(nRegId);
    }

    public Boolean RejectRegister(ulong nRegId, int nStatusCode, String sReasonPhrase)
    {
        return m_objServerVoAI.RejectRegister(nRegId, nStatusCode, sReasonPhrase);
    }

    public Boolean AuthRegister(ulong nRegId)
    {
        return m_objServerVoAI.AuthRegister(nRegId);
    }

    public Boolean AddLine(String sLineName, int nLineType, String sDisplayName, String sUserName, String sAuthLogin, String sAuthPwd, String sDomainRealm, String sServerAddr, int nServerPort, String sAudioCodecList)
    {
        return m_objServerVoAI.AddLine(sLineName, nLineType, sDisplayName, sUserName, sAuthLogin, sAuthPwd, sDomainRealm, sServerAddr, nServerPort, sAudioCodecList);
    }

    public void RemoveLine(String sLineName)
    {
        m_objServerVoAI.RemoveLine(sLineName);
    }

    public Boolean RegisterLine(String sLineName, int nExpire)
    {
        return m_objServerVoAI.RegisterLine(sLineName, nExpire);
    }

    public Boolean UnRegisterLine(String sLineName)
    {
        return m_objServerVoAI.UnRegisterLine(sLineName);
    }

    public Boolean AcceptCallSession(ulong nSessionId, int nTimeout, String sKeyOpenAI, String sPrompt, String sModel, String sVoice, double fOutputAudioSpeed)
    {
        return m_objServerVoAI.AcceptCallSession(nSessionId, nTimeout, sKeyOpenAI, sPrompt, sModel, sVoice, fOutputAudioSpeed);
    }

    public Boolean RejectCallSession(ulong nSessionId, int nStatusCode, String sReasonPhrase)
    {
        return m_objServerVoAI.RejectCallSession(nSessionId, nStatusCode, sReasonPhrase);
    }

    public Boolean CloseCallSession(ulong nSessionId)
    {
        return m_objServerVoAI.CloseCallSession(nSessionId);
    }

    public ulong DialCallSession(String sCallerName, String sCallerId, String sDialNo, String sToPeerName, int nTimeout)
    {
        return m_objServerVoAI.DialCallSession(sCallerName, sCallerId, sDialNo, sToPeerName, nTimeout);
    }

    public Boolean CallSessionSendStatusResponse(ulong nSessionId, int nStatusCode, String sReasonPhrase, String sContactURI)
    {
        return m_objServerVoAI.CallSessionSendStatusResponse(nSessionId, nStatusCode, sReasonPhrase, sContactURI);
    }

    public Boolean SendReqTransferCallBlind(ulong nSessionId, String sToUserName)
    {
        return m_objServerVoAI.SendReqTransferCallBlind(nSessionId, sToUserName);
    }

    public Boolean SendReqTransferCallConsult(ulong nSessionId, ulong nToSessionId)
    {
        return m_objServerVoAI.SendReqTransferCallConsult(nSessionId, nToSessionId);
    }

    public Boolean AcceptChatMessage(ulong nChatMsgId, String sToPeerName)
    {
        return m_objServerVoAI.AcceptChatMessage(nChatMsgId, sToPeerName);
    }

    public void RejectChatMessage(ulong nChatMsgId, int nStatusCode, String sReasonPhrase)
    {
        m_objServerVoAI.RejectChatMessage(nChatMsgId, nStatusCode, sReasonPhrase);
    }

    public ulong SendChatMessageText(String sMsgFrom, String sMsgTo, String sMsgText, String sToPeerName)
    {
        return m_objServerVoAI.SendChatMessageText(sMsgFrom, sMsgTo, sMsgText, sToPeerName);
    }

    public Boolean AudioSessionLost(Boolean bEnable, int nTimeout)
    {
        return m_objServerVoAI.AudioSessionLost(bEnable, nTimeout);
    }

    public Boolean SetUserAgentName(String sName)
    {
        return m_objServerVoAI.SetUserAgentName(sName);
    }

    public String GetUserAgentName()
    {
        return m_objServerVoAI.GetUserAgentName();
    }

    public Boolean SetSessionNameSDP(String sName)
    {
        return m_objServerVoAI.SetSessionNameSDP(sName);
    }

    public String GetSessionNameSDP()
    {
        return m_objServerVoAI.GetSessionNameSDP();
    }

    public Boolean SendDigitDTMF(ulong nSessionId, String sDigitDTMF, int nTypeDTMF)
    {
        return m_objServerVoAI.SendDigitDTMF(nSessionId, sDigitDTMF, nTypeDTMF);
    }

    public Boolean DetectDigitDTMF(ulong nSessionId, int nTypeDTMF, Boolean bEnable, int nMilliSecTimeout)
    {
        return m_objServerVoAI.DetectDigitDTMF(nSessionId, nTypeDTMF, bEnable, nMilliSecTimeout);
    }

    public Boolean ActivateSemanticVAD(ulong nSessionId, String sEagerness)
    {
        return m_objServerVoAI.ActivateSemanticVAD(nSessionId, sEagerness);
    }

    public Boolean ActivateAcousticVAD(ulong nSessionId, double fThreshold, int nPrefixPadding, int nSilenceDuration)
    {
        return m_objServerVoAI.ActivateAcousticVAD(nSessionId, fThreshold, nPrefixPadding, nSilenceDuration);
    }

    public Boolean UpdateSessionOpenAI(ulong nSessionId, String sPrompt, double fOutputAudioSpeed)
    {
        return m_objServerVoAI.UpdateSessionOpenAI(nSessionId, sPrompt, fOutputAudioSpeed);
    }

    public Boolean SendInputOpenAI(ulong nSessionId, String sInput)
    {
        return m_objServerVoAI.SendInputOpenAI(nSessionId, sInput);
    }

    public Boolean AddFunctionOpenAI(ulong nSessionId, String sFuncName, String sFuncDesc)
    {
        return m_objServerVoAI.AddFunctionOpenAI(nSessionId, sFuncName, sFuncDesc);
    }

    public void RemoveFunctionOpenAI(ulong nSessionId, String sFuncName)
    {
        m_objServerVoAI.RemoveFunctionOpenAI(nSessionId, sFuncName);
    }

    public void RemoveFunctionAllOpenAI(ulong nSessionId)
    {
        m_objServerVoAI.RemoveFunctionAllOpenAI(nSessionId);
    }

    public Boolean AddFunctionParamOpenAI(ulong nSessionId, String sFuncName, String sParamName, String sParamDesc)
    {
        return m_objServerVoAI.AddFunctionParamOpenAI(nSessionId, sFuncName, sParamName, sParamDesc);
    }

    public Boolean AddFunctionParamEnumOpenAI(ulong nSessionId, String sFuncName, String sParamName, String sEnumValue)
    {
        return m_objServerVoAI.AddFunctionParamEnumOpenAI(nSessionId, sFuncName, sParamName, sEnumValue);
    }

    public Boolean SendFunctionResultOpenAI(ulong nSessionId, String sCallId, String sOutput)
    {
        return m_objServerVoAI.SendFunctionResultOpenAI(nSessionId, sCallId, sOutput);
    }

    public Boolean AttackDetectScanSIP(String sDomainName)
    {
        return m_objServerVoAI.AttackDetectScanSIP(sDomainName);
    }

    public Boolean AttackDetectFloodSIP(int nReqRecvLimit)
    {
        return m_objServerVoAI.AttackDetectFloodSIP(nReqRecvLimit);
    }

    public Boolean AttackDetectBruteForceSIP(int nFailureCount, int nFailureInterval)
    {
        return m_objServerVoAI.AttackDetectBruteForceSIP(nFailureCount, nFailureInterval);
    }

    public Boolean CallSessionMuteVoice(ulong nSessionId, Boolean bListen, Boolean bSpeak)
    {
        return m_objServerVoAI.CallSessionMuteVoice(nSessionId, bListen, bSpeak);
    }

    public Boolean SetExtDataREC(ulong nSessionId, String sExtData)
    {
        return m_objServerVoAI.SetExtDataREC(nSessionId, sExtData);
    }

    public Boolean ConnectToServerREC(ulong nSessionId, String sCallerName, String sCallerId, String sDialNo, String sToPeerName, int nTimeout)
    {
        return m_objServerVoAI.ConnectToServerREC(nSessionId, sCallerName, sCallerId, sDialNo, sToPeerName, nTimeout);
    }

    public Boolean MuteCallServerREC(ulong nSessionId, int nMuteType)
    {
        return m_objServerVoAI.MuteCallServerREC(nSessionId, nMuteType);
    }

    public String GetUserAgentNameCall(ulong nSessionId)
    {
        return m_objServerVoAI.GetUserAgentNameCall(nSessionId);
    }

    public Boolean AddVectorStoreSearchOpenAI(ulong nSessionId, string sSearchName, string sSearchDesc, string sVectorStoreId, string sFilters, int nMaxNoResults)
    {
        return m_objServerVoAI.AddVectorStoreSearchOpenAI(nSessionId, sSearchName, sSearchDesc, sVectorStoreId, sFilters, nMaxNoResults);
    }

    public void RemoveVectorStoreSearchOpenAI(ulong nSessionId, string sSearchName)
    {
        m_objServerVoAI.RemoveVectorStoreSearchOpenAI(nSessionId, sSearchName);
    }

    public void RemoveVectorStoreSearchAllOpenAI(ulong nSessionId)
    {
        m_objServerVoAI.RemoveVectorStoreSearchAllOpenAI(nSessionId);
    }

    public Boolean InputTranscriptOpenAI(ulong nSessionId, Boolean bActivate, string sModel, string sNoiseReduction)
    {
        return m_objServerVoAI.InputTranscriptOpenAI(nSessionId, bActivate, sModel, sNoiseReduction);
    }


    #endregion


    #region Events

    protected virtual void OnRegisterUser(string sUserName, string sDomain, string sUserAgent, string sFromIP, int nFromPort, ulong nRegId)
    {
    }

    protected virtual void OnRegisterUserSuccess(string sUserName, string sFromIP, int nFromPort, ulong nRegId)
    {
    }

    protected virtual void OnRegisterUserFailed(string sUserName, string sFromIP, int nFromPort, ulong nRegId)
    {
    }

    protected virtual void OnUnRegisterUser(string sUserName)
    {
    }

    protected virtual void OnCallSessionCreated(ulong nSessionId, int nReasonCode)
    {
    }

    protected virtual void OnCallSessionClosed(ulong nSessionId, int nReasonCode)
    {
    }

    protected virtual void OnCallSessionConnecting(ulong nSessionId, int nStatusCode, string sReasonPhrase)
    {
    }

    protected virtual void OnCallSessionFailed(ulong nSessionId, int nStatusCode, string sReasonPhrase, string sContact)
    {
    }

    protected virtual void OnCallSessionConnected(ulong nSessionId)
    {
    }

    protected virtual void OnIncomingCall(ulong nSessionId, string sCallerName, string sCallerId, string sDialNo, int nFromPeerType, string sFromPeerName, string sUserAgent, string sFromIP, int nFromPort)
    {
    }

    protected virtual void OnCallSessionLost(ulong nSessionId)
    {
    }

    protected virtual void OnCallSessionHangup(ulong nSessionId)
    {
    }

    protected virtual void OnCallSessionTimeout(ulong nSessionId)
    {
    }

    protected virtual void OnCallSessionCancelled(ulong nSessionId)
    {
    }

    protected virtual void OnChatMessageText(ulong nChatMsgId, string sMsgFrom, string sMsgTo, string sMsgText, int nFromPeerType, string sFromPeerName, string sFromIP, int nFromPort)
    {
    }

    protected virtual void OnChatMessageSuccess(ulong nChatMsgId)
    {
    }

    protected virtual void OnChatMessageFailed(ulong nChatMsgId, int nStatusId, string sReasonPhrase)
    {
    }

    protected virtual void OnChatMessageTimeout(ulong nChatMsgId)
    {
    }

    private void OnVaxFunctionCallOpenAI(ulong nSessionId, string sFuncName, string sCallId, System.Array aParamNames, System.Array aParamValues)
    {
        string[] aNames = null;
        string[] aValues = null;

        if (aParamNames != null)
        {
            int nLength = aParamNames.Length;

            aNames = new string[nLength];

            for (int nCount = 0; nCount < nLength; nCount++)
                aNames[nCount] = aParamNames.GetValue(nCount) as string;
        }

        if (aParamValues != null)
        {
            int nLength = aParamValues.Length;
            aValues = new string[nLength];
            
            for (int nCount = 0; nCount < nLength; nCount++)
                aValues[nCount] = aParamValues.GetValue(nCount) as string;
        }

        OnVaxFunctionCallOpenAI(nSessionId, sFuncName, sCallId, aNames, aValues);
    }

    protected virtual void OnVaxFunctionCallOpenAI(ulong nSessionId, string sFuncName, string sCallId, string[] aParamNames, string[] aParamValues)
    {
    }

    protected virtual void OnVaxSessionUpdatedOpenAI(ulong nSessionId)
    {
    }

    protected virtual void OnVaxAudioOutputTranscriptOpenAI(ulong nSessionId, string sTranscript)
    {
    }
    protected virtual void OnVaxAudioInputTranscriptOpenAI(ulong nSessionId, string sTranscript)
    {
    }

    protected virtual void OnVaxResponseDoneUsageOpenAI(ulong nSessionId, int nTotalTokens, int nInputTokens, int nOutputTokens)
    {
    }

    protected virtual void OnVaxStatusOpenAI(ulong nSessionId, int nStatusId, string sStatus)
    {
    }

    protected virtual void OnVaxErrorOpenAI(ulong nSessionId, string sMsg)
    {
    }

    protected virtual void OnLineRegisterTrying(string sLineName)
    {
    }

    protected virtual void OnLineRegisterFailed(string sLineName, int nStatusCode, string sReasonPhrase)
    {
    }

    protected virtual void OnLineRegisterSuccess(string sLineName)
    {
    }

    protected virtual void OnLineUnRegisterTrying(string sLineName)
    {
    }

    protected virtual void OnLineUnRegisterFailed(string sLineName, int nStatusCode, string sReasonPhrase)
    {
    }

    protected virtual void OnLineUnRegisterSuccess(string sLineName)
    {
    }

    protected virtual void OnAttackDetectedScanSIP(string sReqMethod, string sAddrIP, int nAddrPort, int nAddrType)
    {
    }

    protected virtual void OnAttackDetectedFloodSIP(string sAddrIP, int nAddrPort, int nAddrType)
    {
    }

    protected virtual void OnAttackDetectedBruteForceSIP(string sReqMethod, int nAuthFailureCount, string sAddrIP, int nAddrPort, int nAddrType)
    {
    }

    protected virtual void OnSendReqTransferCallTimeout(ulong nSessionId)
    {
    }

    protected virtual void OnSendReqTransferCallAccepted(ulong nSessionId)
    {
    }

    protected virtual void OnSendReqTransferCallFailed(ulong nSessionId, int nStatusCode, string sReasonPhrase)
    {
    }

    protected virtual void OnServerConnectingREC(ulong nSessionId, int nStatusCode, string sReasonPhrase)
    {
    }

    protected virtual void OnServerConnectedREC(ulong nSessionId)
    {
    }

    protected virtual void OnServerFailedREC(ulong nSessionId, int nStatusCode, string sReasonPhrase)
    {
    }

    protected virtual void OnServerTimeoutREC(ulong nSessionId)
    {
    }

    protected virtual void OnServerHungupREC(ulong nSessionId)
    {
    }

    protected virtual void OnVaxVectorSearchStartedOpenAI(ulong nSessionId, string sSearchName, string sQuery)
    {
    }

    protected virtual void OnVaxVectorSearchTryingOpenAI(ulong nSessionId, string sSearchName)
    {
    }

    protected virtual void OnVaxVectorSearchSuccessOpenAI(ulong nSessionId, string sSearchName, string sContent)
    {
    }

    protected virtual void OnVaxVectorSearchFailedOpenAI(ulong nSessionId, string sSearchName, string sMsg)
    {
    }

    //////////////////////////////////////////////////////////////////////////////////////////
    //////////////////////////////////////////////////////////////////////////////////////////

    protected virtual void OnCallSessionErrorLog(ulong nSessionId, int nErrorCode, String sErrorMsg)
    {

    }

    protected virtual void OnVaxErrorLog(String sFuncName, int nErrorCode, String sErrorMsg)
    {

    }
        
    #endregion

}





