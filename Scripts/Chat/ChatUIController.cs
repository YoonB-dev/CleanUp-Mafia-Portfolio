using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.UI;

/// <summary>
/// 채팅 로그와 입력창. 전송은 ChatManager가 한다.
/// </summary>
public class ChatUIController : SceneSingleton<ChatUIController>
{
    [Header("UI")]
    [SerializeField] private CanvasGroup logGroup;
    [SerializeField] private Graphic logBackground;
    [SerializeField] private TMP_Text logText;
    [SerializeField] private GameObject inputRoot;
    [SerializeField] private TMP_InputField inputField;
    [SerializeField] private TMP_Text channelLabel;

    [Header("입력")]
    [SerializeField] private InputActionReference openChatAction;

    [Header("설정")]
    [SerializeField] private int maxLines = 30;
    [SerializeField] private float logHideDelay = 10f;
    [SerializeField] private Color allColor = Color.white;
    [SerializeField] private Color mafiaColor = new Color(1f, 0.45f, 0.45f);
    [SerializeField] private Color systemColor = new Color(1f, 0.85f, 0.3f);

    private readonly List<string> _lines = new();

    public bool IsOpen => _isOpen;

    private float _backgroundAlpha;
    private ChatChannel _channel = ChatChannel.All;
    private bool _isOpen;
    private int _openedFrame = -1;
    private float _hideTime;

    private void Start()
    {
        _backgroundAlpha = logBackground.color.a;
        ShowBackground(false);

        logGroup.alpha = 0f;
        inputRoot.SetActive(false);
        UpdateChannelLabel();
    }

    private void OnEnable()
    {
        openChatAction.action.performed += OnOpenChat;
        openChatAction.action.Enable();
        LocalizationSettings.SelectedLocaleChanged += OnLocaleChanged;
    }

    private void OnDisable()
    {
        openChatAction.action.performed -= OnOpenChat;
        LocalizationSettings.SelectedLocaleChanged -= OnLocaleChanged;
        if (_isOpen) SetOpen(false);
    }

    public void AddMessage(ChatChannel channel, string senderName, string text)
    {
        bool mafia = channel == ChatChannel.Mafia;
        string color = ColorUtility.ToHtmlStringRGB(mafia ? mafiaColor : allColor);
        string prefix = mafia ? SettingsText.Translate("chat_channel_mafia") + " " : string.Empty;

        AddLine($"<color=#{color}>{prefix}{senderName}: {text}</color>");
    }

    /// <summary>보낸 사람 없이 안내 문구를 채팅창에 띄운다 (방장 위임 등)</summary>
    public void AddSystemMessage(string text)
    {
        AddLine($"<color=#{ColorUtility.ToHtmlStringRGB(systemColor)}>{text}</color>");
    }

    /// <summary>방장이 바뀌어 대기방을 다시 불러올 때 이어 붙이려고 지금까지의 채팅을 넘긴다</summary>
    public List<string> CopyLines() => new(_lines);

    public void RestoreLines(List<string> lines)
    {
        _lines.Clear();
        _lines.AddRange(lines);
        logText.text = string.Join("\n", _lines);
    }

    private void AddLine(string line)
    {
        _lines.Add(line);
        if (_lines.Count > maxLines) _lines.RemoveAt(0);

        logText.text = string.Join("\n", _lines);
        ShowLog();
    }

    private void Update()
    {
        if (!_isOpen)
        {
            if (_hideTime > 0f && Time.unscaledTime >= _hideTime) HideLog();
            return;
        }

        Keyboard keyboard = Keyboard.current;

        // 채팅을 연 프레임의 Enter를 전송으로 다시 먹지 않게
        if (keyboard == null || Time.frameCount == _openedFrame) return;

        // ESC는 PauseMenuController가 받아서 Close를 부른다
        if (keyboard.tabKey.wasPressedThisFrame) ToggleChannel();
        else if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame) Submit();
    }

    public void Close() => SetOpen(false);

    private void OnOpenChat(InputAction.CallbackContext context)
    {
        if (_isOpen) return;

        // 채팅 열기는 PlayerInput을 거치지 않으므로 메뉴가 열려 있는지 직접 본다
        PauseMenuController menu = PauseMenuController.Instance;
        if (menu != null && menu.IsOpen) return;

        // 작업 지시서도 조작을 끊어 두므로, 채팅이 닫히며 조작을 되살리지 않게 막는다
        InstructionSheetUI sheet = InstructionSheetUI.Instance;
        if (sheet != null && sheet.IsOpen) return;

        _openedFrame = Time.frameCount;
        SetOpen(true);
    }

    private void Submit()
    {
        string text = inputField.text;
        SetOpen(false);

        if (!string.IsNullOrWhiteSpace(text)) ChatManager.Instance?.Send(text, _channel);
    }

    private void SetOpen(bool open)
    {
        _isOpen = open;
        inputRoot.SetActive(open);
        ShowBackground(open);
        inputField.text = string.Empty;

        if (open)
        {
            if (_channel == ChatChannel.Mafia && !IsLocalMafia()) SetChannel(ChatChannel.All);

            inputField.characterLimit = ChatManager.MAX_MESSAGE_LENGTH;
            inputField.ActivateInputField();
            ShowLog();
        }
        else
        {
            inputField.DeactivateInputField();
            _hideTime = Time.unscaledTime + logHideDelay;
        }

        LocalPlayerInput.SetEnabled(!open);
    }

    private void ToggleChannel()
    {
        if (!IsLocalMafia()) return;

        SetChannel(_channel == ChatChannel.All ? ChatChannel.Mafia : ChatChannel.All);

        // Tab이 포커스와 입력을 건드린 것을 되돌린다
        inputField.text = inputField.text.Replace("\t", string.Empty);
        inputField.ActivateInputField();
        inputField.caretPosition = inputField.text.Length;
    }

    private void SetChannel(ChatChannel channel)
    {
        _channel = channel;
        UpdateChannelLabel();
    }

    private void OnLocaleChanged(Locale locale) => UpdateChannelLabel();

    private void UpdateChannelLabel()
    {
        bool mafia = _channel == ChatChannel.Mafia;
        channelLabel.text = SettingsText.Translate(mafia ? "chat_channel_mafia" : "chat_channel_all");
        channelLabel.color = mafia ? mafiaColor : allColor;
    }

    // 배경판은 채팅창을 연 동안만. 로그 글자는 알림처럼 잠깐 떴다 사라진다
    private void ShowBackground(bool visible)
    {
        Color color = logBackground.color;
        color.a = visible ? _backgroundAlpha : 0f;
        logBackground.color = color;
    }

    private void ShowLog()
    {
        logGroup.alpha = 1f;
        _hideTime = _isOpen ? 0f : Time.unscaledTime + logHideDelay;
    }

    private void HideLog()
    {
        logGroup.alpha = 0f;
        _hideTime = 0f;
    }

    // 로비 캐릭터에는 RoleManager가 없다
    private static bool IsLocalMafia()
    {
        NetworkObject player = LocalPlayerInput.Local;

        return player != null
            && player.TryGetComponent(out RoleManager role)
            && role.CurrentRole == PlayerRole.Mafia;
    }
}
