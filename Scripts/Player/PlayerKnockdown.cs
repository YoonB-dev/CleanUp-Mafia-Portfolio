using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 피격당한 플레이어를 래그돌 상태로 날려보내고 잠시 뒤 일으켜 세운다 (서버 권위). <br/>
/// 이동은 서버에서만 계산되므로 SetLimp로 충분하지만, 시점은 Owner가 돌리므로 상태를 동기화한다.
/// </summary>
public class PlayerKnockdown : NetworkBehaviour
{
    private const float GROUND_HOLD_TIME = 1.0f;    // 지면에 계속 닿아 있어야 일어나는 시간
    private const float CONTACT_GAP = 0.1f;         // 구르며 잠깐 뜨는 건 끊긴 걸로 안 본다
    private const float MAX_DOWN_DURATION = 5.0f;   // 접지가 영영 안 잡혀도(낙사 등) 이 시간이면 일어난다
    private const float LANDING_GRACE = 0.1f;       // 다이빙은 발이 땅에 닿은 채 출발하므로 이 시간 안의 접촉은 무시

    // 쓰러짐 상태. 서버가 기록하고 Owner가 시점 조작을 막는 데 쓴다
    private readonly NetworkVariable<bool> _isDownNet = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    [Header("Sound")]
    [Tooltip("쓰러진 동안(펀치에 맞음·돌진) 벽·땅에 부딪힐 때 나는 소리 (SO/Sound/SFX/DiveImpactSound)")]
    [SerializeField] private SoundData impactSound;
    [Tooltip("이보다 느리게 닿으면 소리를 내지 않는다 (m/s). 바닥을 구르며 스치는 소리까지 나지 않게")]
    [SerializeField] private float minImpactSpeed = 3f;
    [Tooltip("이 속도(m/s) 이상이면 가장 크게")]
    [SerializeField] private float fullImpactSpeed = 9f;
    [Tooltip("충돌음끼리 최소 간격 (초). 뼈 여러 개가 한꺼번에 닿아도 한 번만 나게")]
    [SerializeField] private float impactSoundInterval = 0.35f;

    private RagdollDriver _driver;
    private float _nextImpactSoundTime;  // [서버]
    private FirstPersonLook _firstPersonLook;
    private PlayerInteraction _playerInteraction;
    private PlayerGrab _playerGrab;
    private float _groundedSince;  // [서버] 연속 접지가 시작된 시각. 끊기면 무한대로 되돌린다
    private float _downStartTime;  // [서버] 쓰러진 시각(상한 판정용)
    private bool _stayDown;        // [서버] 게임 종료 무대처럼 다시 일어나지 않게 할 때

    /// <summary>쓰러져 있는지</summary>
    public bool IsDown => _isDownNet.Value;

    /// <summary>
    /// RagdollDriver가 Player에서 분리(SetParent)되기 전에 자신을 넘긴다.
    /// </summary>
    /// <param name="driver">이 플레이어의 래그돌 드라이버</param>
    public void BindRagdoll(RagdollDriver driver)
    {
        if (_driver != null) _driver.Impacted -= OnRagdollImpacted;
        _driver = driver;
        if (_driver != null) _driver.Impacted += OnRagdollImpacted;
    }

    // [서버] 뼈마다 따로 오므로 한꺼번에 몰려온다. 일정 간격으로 한 번만, 세게 부딪힐수록 크게 낸다.
    // 쓰러지는 순간 이미 닿아 있던 발밑 바닥은 빼려고 쓰러진 직후 잠깐은 무시한다
    private void OnRagdollImpacted(Vector3 point, float speed)
    {
        if (!IsServer || !_isDownNet.Value || Time.time < _downStartTime + LANDING_GRACE) return;
        if (speed < minImpactSpeed || Time.time < _nextImpactSoundTime) return;

        _nextImpactSoundTime = Time.time + impactSoundInterval;
        float volume = Mathf.Lerp(0.4f, 1f, Mathf.InverseLerp(minImpactSpeed, fullImpactSpeed, speed));
        PlayImpactSoundClientRpc(point, volume);
    }

    [ClientRpc]
    private void PlayImpactSoundClientRpc(Vector3 point, float volume)
    {
        if (impactSound != null && SoundManager.Instance != null) SoundManager.Instance.PlaySFXAt(impactSound, point, volume);
    }

    private void Awake()
    {
        _firstPersonLook = GetComponent<FirstPersonLook>();
        _playerInteraction = GetComponent<PlayerInteraction>();
        _playerGrab = GetComponent<PlayerGrab>();
    }

    public override void OnNetworkSpawn()
    {
        _isDownNet.OnValueChanged += OnDownChanged;
        ApplyOwnerLook(_isDownNet.Value);
    }

    public override void OnNetworkDespawn()
    {
        _isDownNet.OnValueChanged -= OnDownChanged;
        if (_driver != null) _driver.Impacted -= OnRagdollImpacted;
    }

    /// <summary>
    /// 피격. 래그돌을 흐물흐물하게 풀고 통째로 날려보낸다. [서버 전용] <br/>
    /// 쓰러진 상태에서 또 맞으면 기상 시각이 밀리고 속도가 추가된다.
    /// </summary>
    /// <param name="velocity">날아갈 속도 변화량 (m/s)</param>
    /// <param name="angularVelocity">날아가며 도는 각속도 (rad/s, 월드)</param>
    /// <param name="stayDown">true면 다시 일어나지 않는다 (게임 종료 연출용)</param>
    public void ServerKnockdown(Vector3 velocity, Vector3 angularVelocity, bool stayDown = false)
    {
        if (!IsServer || _driver == null)
        {
            return;
        }

        _stayDown |= stayDown;
        EnterDown(dropItem: true, diving: false);
        _driver.ApplyKnockbackVelocity(velocity);
        _driver.ApplyKnockbackSpin(angularVelocity);
    }

    /// <summary>
    /// 스스로 앞으로 몸을 던지는 다이빙. 넉다운과 같은 흐름이되 아이템은 유지. [서버 전용]
    /// </summary>
    /// <param name="velocity">날아갈 속도 변화량 (m/s)</param>
    /// <param name="angularVelocity">몸을 앞으로 눕히는 각속도 (rad/s, 월드)</param>
    public void ServerDive(Vector3 velocity, Vector3 angularVelocity)
    {
        if (!IsServer || _driver == null)
        {
            return;
        }

        EnterDown(dropItem: false, diving: true);
        _driver.SetBonesVelocity(velocity);
        _driver.ApplyKnockbackSpin(angularVelocity);
    }

    // 다운 상태 진입. 이미 다운이면 착지 판정부터 다시 시작한다.
    private void EnterDown(bool dropItem, bool diving)
    {
        _groundedSince = float.PositiveInfinity;
        _downStartTime = Time.time;

        if (_isDownNet.Value)
        {
            return;
        }

        _isDownNet.Value = true;
        _driver.SetLimp(true, diving);

        // 쓰러지면서 잡고 있던 상대를 놓는다(다이빙도 몸을 던지기 전에 여기서 놓임)
        if (_playerGrab != null)
        {
            _playerGrab.ServerReleaseGrab();
        }

        // 끌던 오브젝트도 놓는다 (피격으로 날아갈 때 오브젝트가 끌려오지 않게)
        if (_playerInteraction != null)
        {
            _playerInteraction.ServerReleaseDrag();
        }

        if (dropItem && _playerInteraction != null)
        {
            _playerInteraction.ServerDropHeldItem();
        }
    }

    private void Update()
    {
        if (!IsServer || !_isDownNet.Value || _stayDown)
        {
            return;
        }

        // 다이빙 출발 직후의 접촉은 빼고, 접지가 끊기지 않고 이어지는 동안만 시간을 센다
        bool grounded = _driver != null
            && _driver.LastContactTime > _downStartTime + LANDING_GRACE
            && Time.time - _driver.LastContactTime <= CONTACT_GAP;

        if (!grounded)
        {
            _groundedSince = float.PositiveInfinity;   // 다시 뜨면 처음부터
        }
        else if (_groundedSince > Time.time)
        {
            _groundedSince = Time.time;                // 접지 시작
        }

        bool heldLongEnough = Time.time - _groundedSince >= GROUND_HOLD_TIME;
        bool timedOut = Time.time - _downStartTime >= MAX_DOWN_DURATION;
        if (!heldLongEnough && !timedOut)
        {
            return;
        }

        // 앵커가 다시 몸을 세우면서 일어난다
        _isDownNet.Value = false;
        _driver.SetLimp(false);
    }

    private void OnDownChanged(bool previous, bool current)
    {
        ApplyOwnerLook(current);
    }

    // 쓰러진 동안 Owner의 시점 조작을 막는다. 카메라는 몸을 따라 같이 굴러간다
    private void ApplyOwnerLook(bool isDown)
    {
        if (!IsOwner || _firstPersonLook == null)
        {
            return;
        }

        _firstPersonLook.enabled = !isDown;
    }
}
