using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// ESC로 여는 일시정지 메뉴. 로비와 인게임 씬에 하나씩 둔다.
/// 멀티플레이라 시간을 멈추지는 않고 조작만 끊는다.
/// </summary>
public class PauseMenuController : SceneSingleton<PauseMenuController>
{
    [SerializeField] private GameObject menuRoot;
    [SerializeField] private Button dimButton;                  // 패널 밖을 눌러도 닫힌다
    [SerializeField] private SettingsUIController settingsUI;   // 비워 두면 씬에서 찾는다
    [SerializeField] private string leaveSceneName = "MainScene";
    [SerializeField] private bool suspendPlayer = true;   // 플레이어가 없는 씬에서는 끈다

    public bool IsOpen => _isOpen;

    // 설정 창은 다른 캔버스에 있어 프리팹 안에서는 참조를 들고 있을 수 없다
    private SettingsUIController Settings => settingsUI != null ? settingsUI : SettingsUIController.Instance;

    private bool _isOpen;

    private void Start()
    {
        if (dimButton != null) dimButton.onClick.AddListener(Resume);

        menuRoot.SetActive(false);
    }

    private void OnDisable()
    {
        if (_isOpen) SetOpen(false);
    }

    private void Update()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null || !keyboard.escapeKey.wasPressedThisFrame) return;

        // 키 입력을 기다리는 중이면 그쪽이 취소로 먹는다
        if (RebindEntryUI.ConsumesEscape) return;

        // 결과 화면에서는 ESC를 받지 않는다. 메뉴를 열었다 닫으면 커서가 잠겨 "대기방으로" 버튼을 못 누른다
        InGameUIController inGameUI = InGameUIController.Instance;
        if (inGameUI != null && inGameUI.IsResultShown) return;

        // 작업 지시서가 열려 있으면 그것부터 닫는다
        InstructionSheetUI sheet = InstructionSheetUI.Instance;
        if (sheet != null && sheet.IsOpen)
        {
            sheet.Close();
            return;
        }

        // 채팅창이 열려 있으면 그것부터 닫는다
        ChatUIController chat = ChatUIController.Instance;
        if (chat != null && chat.IsOpen)
        {
            chat.Close();
            return;
        }

        VoteUIController vote = VoteUIController.Instance;
        if (vote != null && vote.IsOpen)
        {
            vote.Close();
            return;
        }

        SettingsUIController settings = Settings;
        if (settings != null && settings.IsOpen)
        {
            settings.Close();
            return;
        }

        SetOpen(!_isOpen);
    }

    // 버튼에서 연결한다
    public void Resume() => SetOpen(false);

    public void OpenSettings()
    {
        SettingsUIController settings = Settings;
        if (settings != null) settings.Open();
    }

    public void Leave()
    {
        SetOpen(false);

        // 스스로 나가는 것이므로 방장 위임으로 다시 접속하지 않는다
        if (HostMigration.Instance != null) HostMigration.Instance.Forget();
        if (NetworkManager.Singleton != null) NetworkManager.Singleton.Shutdown();
        SceneManager.LoadScene(leaveSceneName);
    }

    private void SetOpen(bool open)
    {
        _isOpen = open;
        menuRoot.SetActive(open);

        SettingsUIController settings = Settings;
        if (!open && settings != null) settings.Close();

        Cursor.lockState = open ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = open;

        if (suspendPlayer) LocalPlayerInput.SetEnabled(!open);
    }
}
