using System.Collections;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.Localization;
using UnityEngine.Localization.Components;
using UnityEngine.Localization.Settings;
using UnityEngine.Serialization;
using UnityEngine.UI;

/// <summary>
/// T로 여닫는 작업 지시서(조작법 안내). 로비와 인게임 씬에 하나씩 둔다.
/// 1쪽 작업 지시서, 2쪽 물품 안내(쓰레기·도구), 3쪽 긴급 투표는 모두가 보고, 마피아는 뒤에 기밀 작업 지시서가 한 장 더 붙는다. 로비는 직업이 없으므로 3쪽까지만 보인다.
/// 열려 있는 동안은 일시정지 메뉴처럼 조작을 끊고, A/D 또는 마우스로 장을 넘긴다. ESC는 PauseMenuController가 받아서 Close를 부른다.
/// </summary>
public class InstructionSheetUI : SceneSingleton<InstructionSheetUI>
{
    [System.Serializable]
    private struct KeyLabel
    {
        public TMP_Text text;
        public string action;
        [Tooltip("WASD처럼 묶인 바인딩의 방향 이름(up/down/left/right). 비워 두면 첫 바인딩")]
        public string part;
    }

    // 물품 안내의 점수 칩. 쓰레기 SO의 점수를 그대로 보여 주므로 점수를 바꾸면 지시서도 따라 바뀐다
    [System.Serializable]
    private struct CatalogScore
    {
        public LocalizeStringEvent text;   // "+{0}점"
        [Tooltip("같은 칸에 묶인 쓰레기들. 점수가 서로 다르면 최소~최대로 보인다")]
        public TrashData[] trash;
    }

    private enum VoteValue { Countdown, Voting, Cooldown, Penalty }

    // 긴급 투표 쪽의 시간 문구. VoteSettings 값을 그대로 보여 주므로 투표 시간을 바꾸면 지시서도 따라 바뀐다
    [System.Serializable]
    private struct VoteText
    {
        public LocalizeStringEvent text;   // {0}에 초가 들어간다
        public VoteValue value;
    }

    [SerializeField] private bool allowMafiaPage = true;   // 직업이 없는 로비에서는 끈다

    [Header("탭")]
    [SerializeField] private RectTransform tab;
    [SerializeField] private RectTransform tabKeyCap;      // 처음에 깜빡여서 눈에 띄게 한다
    [SerializeField] private GameObject tabMafiaSheet;     // 마피아면 탭 뒤에 한 장이 더 비친다
    [SerializeField] private float hintDuration = 12f;

    [Header("지시서")]
    [SerializeField] private CanvasGroup overlay;
    [SerializeField] private RectTransform sheet;
    [SerializeField] private Button dimButton;             // 지시서 밖을 눌러도 닫힌다
    [FormerlySerializedAs("mafiaBackSheet")]
    [SerializeField] private GameObject backSheet;         // 뒷장이 남아 있으면 종이 뒤로 한 장이 삐져나온다
    [SerializeField] private GameObject backSheetEdge;     // 그 가장자리의 주황 띠. 마피아만 (맨 뒤에 기밀 지시서가 있다는 표시)
    [SerializeField] private GameObject citizenPage;       // 1쪽
    [SerializeField] private GameObject catalogPage;       // 2쪽 물품 안내
    [SerializeField] private GameObject votePage;          // 3쪽 긴급 투표
    [SerializeField] private GameObject mafiaPage;         // 4쪽 (마피아만)
    [SerializeField] private TMP_Text pageLabel;
    [SerializeField] private Button prevButton;
    [SerializeField] private Button nextButton;
    [SerializeField] private float animDuration = 0.22f;

    [Header("문구")]
    [SerializeField] private LocalizeStringEvent[] winPercentTexts;   // {0}에 시민 승리 기준(%)이 들어간다
    [SerializeField] private int fallbackWinPercent = 80;              // InGameManager가 없는 로비용
    [SerializeField] private LocalizedString leftClickLabel;
    [SerializeField] private string actionMapName = "Player";
    [SerializeField] private KeyLabel[] keyLabels;

    [Header("물품 안내")]
    [SerializeField] private CatalogScore[] catalogScores;
    [SerializeField] private LocalizeStringEvent toolsNote;    // "줍기 {0}, 사용 {1}"
    [SerializeField] private string pickupAction = "Interaction_PickItem";
    [SerializeField] private string useAction = "UseItem";

    [Header("긴급 투표")]
    [Tooltip("VoteManager가 없는 로비에서 읽을 투표 설정 (인게임은 VoteManager의 설정을 쓴다)")]
    [SerializeField] private VoteSettings voteSettings;
    [SerializeField] private VoteText[] voteTexts;

    public bool IsOpen => _isOpen;

    private bool _isOpen;
    private bool _isMafia;
    private bool _hidden;
    private int _page = 1;
    private bool _hintStarted;
    private float _hintEndTime;
    private Vector3 _sheetHome;
    private Coroutine _anim;
    private RoleManager _localRole;
    private readonly object[] _args = new object[1];

    private int PageCount => _isMafia ? 4 : 3;

    protected override void Awake()
    {
        base.Awake();

        _sheetHome = sheet.localPosition;
        if (dimButton != null) dimButton.onClick.AddListener(Close);
        if (prevButton != null) prevButton.onClick.AddListener(() => SetPage(_page - 1));
        if (nextButton != null) nextButton.onClick.AddListener(() => SetPage(_page + 1));

        ApplyOpenInstant(false);
        RefreshPages();
    }

    private void Start()
    {
        LocalizationSettings.SelectedLocaleChanged += OnLocaleChanged;
    }

    protected override void OnDestroy()
    {
        LocalizationSettings.SelectedLocaleChanged -= OnLocaleChanged;
        if (_localRole != null) _localRole.RoleChanged -= OnRoleChanged;
        base.OnDestroy();
    }

    private void OnDisable()
    {
        if (_isOpen) SetOpen(false);
    }

    private void Update()
    {
        // 결과 화면이 뜨면 지시서는 치운다
        if (IsGameOver())
        {
            if (!_hidden) Hide();
            return;
        }

        TrackLocalRole();
        UpdateHint();

        Keyboard keyboard = Keyboard.current;
        if (keyboard == null) return;

        if (keyboard.tKey.wasPressedThisFrame)
        {
            if (_isOpen) SetOpen(false);
            else if (CanOpen()) SetOpen(true);
            return;
        }

        if (!_isOpen) return;

        if (keyboard.aKey.wasPressedThisFrame || keyboard.leftArrowKey.wasPressedThisFrame) SetPage(_page - 1);
        else if (keyboard.dKey.wasPressedThisFrame || keyboard.rightArrowKey.wasPressedThisFrame) SetPage(_page + 1);
    }

    // PauseMenuController가 ESC로 부른다
    public void Close()
    {
        if (_isOpen) SetOpen(false);
    }

    // 결과 화면이 커서를 풀어 두므로 여기서는 커서를 건드리지 않는다
    private void Hide()
    {
        _hidden = true;

        if (_isOpen)
        {
            _isOpen = false;
            LocalPlayerInput.SetEnabled(true);
        }

        if (_anim != null) StopCoroutine(_anim);
        _anim = null;
        ApplyOpenInstant(false);
        tab.gameObject.SetActive(false);
    }

    private static bool IsGameOver()
    {
        GameTimerManager timer = GameTimerManager.Instance;
        return timer != null && timer.IsSpawned && timer.CurrentState.Value == TimerState.Expired;
    }

    // 인게임 시작 직후 직업 안내가 떠 있는 동안. 지시서가 그 패널 밑에 깔리므로 이때는 열지 않는다
    private static bool IsRevealingRole()
    {
        GameTimerManager timer = GameTimerManager.Instance;
        if (timer == null) return false;   // 로비

        return !timer.IsSpawned || timer.CurrentState.Value == TimerState.None || timer.CurrentState.Value == TimerState.Ready;
    }

    private static bool CanOpen()
    {
        if (IsRevealingRole()) return false;

        PauseMenuController menu = PauseMenuController.Instance;
        if (menu != null && menu.IsOpen) return false;

        ChatUIController chat = ChatUIController.Instance;
        if (chat != null && chat.IsOpen) return false;

        SettingsUIController settings = SettingsUIController.Instance;
        if (settings != null && settings.IsOpen) return false;

        // 로비의 입력창 같은 데에 글자를 치는 중이면 T를 먹지 않는다
        EventSystem eventSystem = EventSystem.current;
        GameObject selected = eventSystem != null ? eventSystem.currentSelectedGameObject : null;
        if (selected != null && selected.TryGetComponent(out TMP_InputField field) && field.isFocused) return false;

        return true;
    }

    private void SetOpen(bool open)
    {
        if (_isOpen == open) return;
        _isOpen = open;

        if (open)
        {
            _hintEndTime = 0f;   // 한 번 열어 봤으면 더 깜빡일 필요 없다
            _page = 1;
            RefreshPages();
            RefreshKeyLabels();
            RefreshWinPercent();
            RefreshCatalog();
            RefreshVoteTexts();
        }

        Cursor.lockState = open ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = open;
        LocalPlayerInput.SetEnabled(!open);

        if (_anim != null) StopCoroutine(_anim);
        if (isActiveAndEnabled) _anim = StartCoroutine(Animate(open));
        else ApplyOpenInstant(open);
    }

    private void SetPage(int page)
    {
        if (!_isOpen) return;

        int clamped = Mathf.Clamp(page, 1, PageCount);
        if (clamped == _page) return;

        _page = clamped;
        RefreshPages();
    }

    private void RefreshPages()
    {
        if (_page > PageCount) _page = PageCount;

        citizenPage.SetActive(_page == 1);
        catalogPage.SetActive(_page == 2);
        if (votePage != null) votePage.SetActive(_page == 3);
        mafiaPage.SetActive(_page == 4);
        backSheet.SetActive(_page < PageCount);
        if (backSheetEdge != null) backSheetEdge.SetActive(_isMafia);
        tabMafiaSheet.SetActive(_isMafia);

        pageLabel.text = $"{_page} / {PageCount}";
        prevButton.gameObject.SetActive(_page > 1);
        nextButton.gameObject.SetActive(_page < PageCount);
    }

    // 탭 자리에서 커지며 펼쳐지고, 닫을 때는 탭 자리로 접혀 들어간다
    private IEnumerator Animate(bool open)
    {
        overlay.gameObject.SetActive(true);
        overlay.blocksRaycasts = open;
        overlay.interactable = open;

        Vector3 from = TabCenter();
        float startT = InverseLerp(sheet.localScale.x);
        float t = open ? startT : 1f - startT;

        while (t < 1f)
        {
            t += Time.unscaledDeltaTime / animDuration;
            float k = Mathf.Clamp01(t);
            float eased = open ? EaseOutBack(k) : 1f - k * k;
            ApplyProgress(eased, from);
            yield return null;
        }

        ApplyOpenInstant(open);
        _anim = null;
    }

    private const float ClosedScale = 0.12f;

    private float InverseLerp(float scale) => Mathf.Clamp01((scale - ClosedScale) / (1f - ClosedScale));

    // 탭과 지시서는 같은 화면 크기의 부모 아래에 있으므로 로컬 좌표를 그대로 비교할 수 있다
    private Vector3 TabCenter()
    {
        Vector2 offset = Vector2.Scale(new Vector2(0.5f, 0.5f) - tab.pivot, tab.rect.size);
        return tab.localPosition + (Vector3)offset;
    }

    private void ApplyProgress(float p, Vector3 from)
    {
        sheet.localScale = Vector3.one * Mathf.LerpUnclamped(ClosedScale, 1f, p);
        sheet.localPosition = Vector3.LerpUnclamped(from, _sheetHome, p);
        overlay.alpha = Mathf.Clamp01(p * 1.5f);
    }

    private void ApplyOpenInstant(bool open)
    {
        sheet.localScale = open ? Vector3.one : Vector3.one * ClosedScale;
        sheet.localPosition = open ? _sheetHome : TabCenter();
        overlay.alpha = open ? 1f : 0f;
        overlay.blocksRaycasts = open;
        overlay.interactable = open;
        overlay.gameObject.SetActive(open);

        // 열려 있는 동안 탭은 흐리게 둔다
        tab.localScale = Vector3.one;
        if (tab.TryGetComponent(out CanvasGroup tabGroup)) tabGroup.alpha = open ? 0.35f : 1f;
    }

    private static float EaseOutBack(float x)
    {
        const float c1 = 1.4f;
        const float c3 = c1 + 1f;
        return 1f + c3 * Mathf.Pow(x - 1f, 3f) + c1 * Mathf.Pow(x - 1f, 2f);
    }

    private void UpdateHint()
    {
        if (tabKeyCap == null) return;

        // 직업 안내가 끝나고 실제로 움직일 수 있게 된 뒤부터 깜빡인다
        if (!_hintStarted && !IsRevealingRole())
        {
            _hintStarted = true;
            _hintEndTime = Time.unscaledTime + hintDuration;
        }

        bool hinting = !_isOpen && Time.unscaledTime < _hintEndTime;
        float scale = hinting ? 1f + 0.12f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 4f)) : 1f;
        tabKeyCap.localScale = Vector3.one * scale;
    }

    #region 직업

    // 배정 목록은 서버에만 있으므로 내 플레이어의 RoleManager에서 읽는다. 값이 늦게 복제될 수 있어 변경 이벤트도 받는다
    private void TrackLocalRole()
    {
        if (!allowMafiaPage || _localRole != null) return;

        NetworkManager network = NetworkManager.Singleton;
        NetworkObject player = network != null ? network.LocalClient?.PlayerObject : null;
        if (player == null || !player.TryGetComponent(out RoleManager role)) return;

        _localRole = role;
        _localRole.RoleChanged += OnRoleChanged;
        OnRoleChanged(_localRole.CurrentRole);
    }

    private void OnRoleChanged(PlayerRole role)
    {
        _isMafia = allowMafiaPage && role == PlayerRole.Mafia;
        RefreshPages();
    }

    #endregion

    #region 문구

    private void OnLocaleChanged(Locale locale)
    {
        if (!_isOpen) return;

        RefreshKeyLabels();
        RefreshCatalog();
    }

    // 점수는 쓰레기 SO에서, 줍기·사용 키는 현재 바인딩에서 읽는다
    private void RefreshCatalog()
    {
        foreach (CatalogScore score in catalogScores)
        {
            if (score.text == null || score.trash == null || score.trash.Length == 0) continue;

            int min = int.MaxValue, max = int.MinValue;
            foreach (TrashData trash in score.trash)
            {
                if (trash == null) continue;
                min = Mathf.Min(min, trash.score);
                max = Mathf.Max(max, trash.score);
            }
            if (min > max) continue;

            score.text.StringReference.Arguments = new object[] { min == max ? min.ToString() : $"{min}~{max}" };
            score.text.RefreshString();
        }

        if (toolsNote == null) return;

        InputActionMap map = SettingsManager.Instance.InputActions?.FindActionMap(actionMapName);
        InputAction pickup = map?.FindAction(pickupAction);
        InputAction use = map?.FindAction(useAction);
        toolsNote.StringReference.Arguments = new object[]
        {
            (pickup != null ? DisplayOf(pickup, null) : null) ?? "E",
            (use != null ? DisplayOf(use, null) : null) ?? leftClickLabel.GetLocalizedString(),
        };
        toolsNote.RefreshString();
    }

    private void RefreshVoteTexts()
    {
        VoteManager vote = VoteManager.Instance;
        VoteSettings settings = vote != null && vote.Settings != null ? vote.Settings : voteSettings;
        if (settings == null || voteTexts == null) return;

        foreach (VoteText entry in voteTexts)
        {
            if (entry.text == null) continue;

            float seconds = entry.value switch
            {
                VoteValue.Countdown => settings.countdownSeconds,
                VoteValue.Voting => settings.votingSeconds,
                VoteValue.Cooldown => settings.cooldownSeconds,
                _ => settings.penaltySeconds,
            };
            entry.text.StringReference.Arguments = new object[] { Mathf.RoundToInt(seconds) };
            entry.text.RefreshString();
        }
    }

    private void RefreshWinPercent()
    {
        InGameManager game = InGameManager.Instance;
        _args[0] = game != null ? Mathf.RoundToInt(game.CitizenWinCleanRatio * 100f) : fallbackWinPercent;

        foreach (LocalizeStringEvent text in winPercentTexts)
        {
            if (text == null) continue;

            text.StringReference.Arguments = _args;
            text.RefreshString();
        }
    }

    // 키 설정에서 바꾼 키가 바로 보이도록 열 때마다 현재 바인딩에서 읽는다
    private void RefreshKeyLabels()
    {
        // KeyBindingsPanel과 같은 에셋을 봐야 저장된 키 변경이 반영된다
        InputActionAsset asset = SettingsManager.Instance.InputActions;
        InputActionMap map = asset != null ? asset.FindActionMap(actionMapName) : null;
        if (map == null) return;

        foreach (KeyLabel label in keyLabels)
        {
            if (label.text == null) continue;

            InputAction action = map.FindAction(label.action);
            if (action == null) continue;

            string display = DisplayOf(action, label.part);
            if (!string.IsNullOrEmpty(display)) label.text.text = display;
        }
    }

    private string DisplayOf(InputAction action, string part)
    {
        for (int i = 0; i < action.bindings.Count; i++)
        {
            InputBinding binding = action.bindings[i];
            if (binding.isComposite) continue;

            bool matches = string.IsNullOrEmpty(part)
                ? !binding.isPartOfComposite
                : binding.isPartOfComposite && binding.name == part;
            if (!matches) continue;

            if (binding.effectivePath == "<Mouse>/leftButton" && !leftClickLabel.IsEmpty) return leftClickLabel.GetLocalizedString();

            return action.GetBindingDisplayString(i);
        }

        return null;
    }

    #endregion
}
