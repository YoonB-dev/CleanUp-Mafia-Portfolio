using TMPro;
using UnityEngine;

/// <summary>
/// 인게임 시작 직후 직업 안내가 떠 있는 동안 화면 위쪽에 "작업 시작까지 N"을 보여 준다.
/// 서버 타이머(GameTimerManager)의 Ready 상태와 남은 시간을 그대로 읽으므로 모두에게 같은 숫자가 보이고, 0이 되면 다 같이 움직이기 시작한다.
/// 숫자가 바뀔 때마다 똑딱 소리를, 카운트가 끝나 작업이 시작되면 시작 소리를 낸다.
/// </summary>
public class StartCountdownUI : MonoBehaviour
{
    [SerializeField] private GameObject card;
    [SerializeField] private TMP_Text numberText;
    [SerializeField] private float punchScale = 1.3f;     // 숫자가 바뀔 때 튀어 오르는 크기
    [SerializeField] private float punchDuration = 0.25f;

    [Header("Sound")]
    [Tooltip("숫자가 하나 줄어들 때마다 나는 소리 (SO/Sound/SFX/CountdownTick)")]
    [SerializeField] private SoundData tickSound;
    [Tooltip("카운트가 끝나 작업이 시작될 때 나는 소리 (SO/Sound/SFX/CountdownGo)")]
    [SerializeField] private SoundData goSound;

    private int _shown = -1;
    private float _punchTime = -1f;
    private bool _sawCountdown;

    private void Awake() => card.SetActive(false);

    private void Update()
    {
        GameTimerManager timer = GameTimerManager.Instance;
        bool counting = timer != null && timer.IsSpawned
            && timer.CurrentState.Value == TimerState.Ready && timer.RemainingTime.Value > 0;

        // 카운트를 본 뒤 실제로 작업이 시작됐을 때 한 번. 남은 시간 0과 상태 전환이 다른 프레임에 도착해도 놓치지 않게 상태로 판단한다
        if (counting) _sawCountdown = true;
        if (_sawCountdown && timer != null && timer.IsSpawned && timer.CurrentState.Value == TimerState.Playing)
        {
            _sawCountdown = false;
            PlaySound(goSound);
        }

        if (card.activeSelf != counting) card.SetActive(counting);
        if (!counting)
        {
            _shown = -1;
            return;
        }

        int seconds = timer.RemainingTime.Value;
        if (seconds != _shown)
        {
            _shown = seconds;
            numberText.text = seconds.ToString();
            _punchTime = Time.unscaledTime;
            PlaySound(tickSound);
        }

        float t = Mathf.Clamp01((Time.unscaledTime - _punchTime) / punchDuration);
        float eased = 1f - (1f - t) * (1f - t);
        numberText.rectTransform.localScale = Vector3.one * Mathf.Lerp(punchScale, 1f, eased);
    }

    private static void PlaySound(SoundData sound)
    {
        if (sound != null && SoundManager.Instance != null) SoundManager.Instance.PlaySFX(sound);
    }
}
