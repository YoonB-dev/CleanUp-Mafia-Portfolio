using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어 발소리. Player 프리팹 루트에 붙인다.
/// 네트워크(RPC)를 쓰지 않고, 각 클라이언트가 동기화된 위치(NetworkTransform)로 모든 플레이어의 이동 거리를 직접 재서
/// 일정 거리마다 발밑 표면(FootstepSurface)에 맞는 소리를 3D로 낸다.
/// 속도가 빠를수록 발소리 간격이 좁아지고 소리도 커지므로, 나중에 달리기가 생겨도 이 코드는 고칠 필요가 없다.
/// </summary>
public class PlayerFootsteps : MonoBehaviour
{
    [SerializeField] private FootstepProfile profile;

    [Header("걸음")]
    [Tooltip("이만큼(m) 이동할 때마다 발소리 1회. 플레이어 크기(스케일 2)에 맞춰 조절")]
    [SerializeField] private float stepDistance = 2f;

    [Tooltip("이 속도(m/s)보다 느리면 발소리를 내지 않음 (미세하게 밀리는 정도는 무시)")]
    [SerializeField] private float minSpeed = 1f;

    [Tooltip("한 프레임에 이보다(m) 많이 움직이면 순간이동(스폰, 위치 보정)으로 보고 무시")]
    [SerializeField] private float teleportDistance = 3f;

    [Tooltip("발 아래로 이 거리(m)까지 바닥이 있으면 땅에 있는 것으로 판단 (없으면 공중이라 무음)")]
    [SerializeField] private float groundCheckDistance = 0.4f;

    [Header("볼륨")]
    [Tooltip("이 속도(m/s)에서 볼륨 배율 1. 더 빠르면(달리기 등) 커지고 느리면 작아짐. 현재 걷기 속도 = 8")]
    [SerializeField] private float referenceSpeed = 8f;

    [Tooltip("속도에 따른 볼륨 배율의 최소/최대")]
    [SerializeField] private Vector2 volumeScaleRange = new Vector2(0.6f, 1.6f);

    [Tooltip("내 캐릭터 발소리에 곱하는 배율 (계속 들리는 소리라 작게)")]
    [Range(0f, 1f)]
    [SerializeField] private float localPlayerVolumeScale = 0.6f;

    [Header("착지")]
    [Tooltip("착지할 때 쿵 소리 (SO/Sound/SFX/LandSound). 발밑 표면의 발소리도 같이 난다")]
    [SerializeField] private SoundData landSound;

    [Tooltip("이 속도(m/s)보다 빠르게 떨어지다 땅에 닿으면 착지 소리")]
    [SerializeField] private float minLandingSpeed = 3f;

    [Tooltip("이 낙하 속도(m/s)에서 착지 소리가 가장 크다")]
    [SerializeField] private float maxLandingSpeed = 15f;

    [Tooltip("착지 소리 뒤 첫 발소리까지의 시간(초). 계속 걷고 있으면 이 시간이 지나자마자 발소리가 난다")]
    [SerializeField] private float landingStepDelay = 0.25f;
    [Tooltip("살짝 얹히듯 착지할 때(사다리 가로대·턱 위로 점프해 올라섬) 발소리 크기. 떨어지는 속도가 거의 없어 쿵 소리는 내지 않고 밟은 표면 소리만 낸다")]
    [SerializeField, Range(0f, 1f)] private float softLandingVolume = 0.7f;

    [Header("이펙트")]
    [Tooltip("걸음마다 발밑에 띄울 이펙트 (루트에 ParticleSystem이 있는 프리팹, 예: Prefab/VFX/Footstep). 비워두면 없음")]
    [SerializeField] private ParticleSystem stepEffect;

    [Tooltip("미리 만들어 돌려쓸 이펙트 개수. (파티클이 사라지는 시간 × 초당 걸음 수)보다 크게. 부족하면 가장 오래된 것부터 끊고 재사용")]
    [SerializeField] private int effectPoolSize = 8;

    [Tooltip("이펙트를 바닥에서 띄우는 높이(m). 파티클이 바닥에 묻혀 보이면 늘림")]
    [SerializeField] private float effectHeightOffset = 0.1f;

    private static readonly IComparer<RaycastHit> HitDistanceComparer =
        Comparer<RaycastHit>.Create((a, b) => a.distance.CompareTo(b.distance));

    private readonly RaycastHit[] _groundHits = new RaycastHit[8];

    private NetworkObject _networkObject;
    private PlayerKnockdown _knockdown;
    private CharacterController _characterController;

    private Vector3 _lastPosition;
    private float _distanceSinceStep;
    private float _smoothedSpeed;

    // 착지 판정. 발밑에 바닥이 없을 때만 공중으로 보므로 계단을 내려가는 건 착지가 아니다
    private float _lastY;
    private bool _airborne;
    private float _fallSpeed;   // 이번 공중 구간에서 가장 빨랐던 하강 속도
    private float _stepBlockedUntil;   // 착지 소리와 발소리가 겹치지 않게 잠시 막는다

    // 걸음 이펙트 풀. 플레이어를 따라다니지 않고 밟은 자리에 남도록 플레이어 밖에 둔다
    private Transform _effectRoot;
    private readonly List<ParticleSystem> _effectPool = new List<ParticleSystem>();
    private int _nextEffectIndex;

    private void Awake()
    {
        _networkObject = GetComponent<NetworkObject>();
        _knockdown = GetComponent<PlayerKnockdown>();
        _characterController = GetComponent<CharacterController>();
    }

    private void OnEnable()
    {
        _lastPosition = transform.position;
        _lastY = _lastPosition.y;
        _airborne = false;
        ResetStride();
    }

    private void OnDestroy()
    {
        // 풀은 플레이어 밖에 있으므로 플레이어가 사라질 때 같이 정리
        if (_effectRoot != null) Destroy(_effectRoot.gameObject);
    }

    private void Update()
    {
        float deltaTime = Time.deltaTime;
        if (deltaTime <= 0f) return;

        UpdateLanding(deltaTime);

        Vector3 position = transform.position;
        Vector3 delta = position - _lastPosition;
        delta.y = 0f;   // 계단/경사에서 위아래 흔들림은 걸음으로 치지 않음
        _lastPosition = position;
        float moved = delta.magnitude;

        // 순간이동이나 넉다운/다이빙(래그돌로 날아가는 중)은 걸음이 아님
        if (moved > teleportDistance || (_knockdown != null && _knockdown.IsDown))
        {
            _smoothedSpeed = 0f;
            ResetStride();
            return;
        }

        // 네트워크 보간으로 프레임마다 이동량이 들쭉날쭉하므로 속도는 부드럽게
        _smoothedSpeed = Mathf.Lerp(_smoothedSpeed, moved / deltaTime, 1f - Mathf.Exp(-10f * deltaTime));
        if (_smoothedSpeed < minSpeed)
        {
            ResetStride();
            return;
        }

        // 공중이거나 막 착지한 직후에는 발소리를 내지 않는다 (착지 직전·직후 발소리가 착지 소리와 겹친다).
        // 막힌 동안 한 걸음을 채워 두므로, 풀리자마자 계속 걷고 있으면 바로 첫 발소리가 난다
        if (_airborne || Time.time < _stepBlockedUntil)
        {
            _distanceSinceStep = stepDistance;
            return;
        }

        _distanceSinceStep += moved;
        if (_distanceSinceStep < stepDistance) return;

        // 한 프레임에 여러 걸음이 쌓여도 한 번만 (소리 몰림 방지)
        _distanceSinceStep = Mathf.Repeat(_distanceSinceStep, stepDistance);
        TryPlayStep();
    }

    // 멈췄다가 다시 걸을 때 반걸음 만에 첫 발소리가 나도록
    private void ResetStride() => _distanceSinceStep = stepDistance * 0.5f;

    private void TryPlayStep()
    {
        if (profile == null) return;

        // 바닥을 못 찾음 = 공중 (점프 등) → 무음, 이펙트 없음
        if (!FindGround(out RaycastHit ground)) return;

        SurfaceType surface = FootstepSurface.Resolve(ground.collider);
        SoundManager.Instance?.PlaySFXAt(profile.GetSound(surface), ground.point, CurrentVolumeScale);
        PlayStepEffect(ground.point, ground.normal);
    }

    // 발밑 바닥 확인 = 땅에 있는지 + 어디를 밟았는지
    private bool FindGround(out RaycastHit ground)
    {
        float rayLength = FootOffset + groundCheckDistance;
        int count = Physics.RaycastNonAlloc(transform.position, Vector3.down, _groundHits, rayLength, ~0, QueryTriggerInteraction.Ignore);
        System.Array.Sort(_groundHits, 0, count, HitDistanceComparer);

        for (int i = 0; i < count; i++)
        {
            if (IsPlayerBody(_groundHits[i].collider)) continue;
            ground = _groundHits[i];
            return true;
        }

        // 사다리 가로대·난간·계단 모서리처럼 몸 가장자리로 걸쳐 서 있으면 가운데 레이는 허공을 찍는다.
        // 몸 굵기만 한 구를 발바닥까지 쓸어서 한 번 더 찾는다
        float radius = BodyRadius * SIDE_PROBE_RADIUS_RATIO;
        float sweep = Mathf.Max(0f, FootOffset - radius) + groundCheckDistance;
        count = Physics.SphereCastNonAlloc(transform.position, radius, Vector3.down, _groundHits, sweep, ~0, QueryTriggerInteraction.Ignore);
        System.Array.Sort(_groundHits, 0, count, HitDistanceComparer);

        for (int i = 0; i < count; i++)
        {
            // 시작부터 겹친 것(옆 벽 등)은 맞은 지점을 알 수 없어 건너뛴다
            if (_groundHits[i].distance <= 0f || IsPlayerBody(_groundHits[i].collider)) continue;
            ground = _groundHits[i];
            return true;
        }

        ground = default;
        return false;
    }

    private const float SIDE_PROBE_RADIUS_RATIO = 0.9f; // 몸보다 살짝 가늘게 쓸어야 옆 벽을 바닥으로 잡지 않는다

    // 캐릭터 캡슐 반지름 (월드)
    private float BodyRadius => _characterController != null
        ? _characterController.radius * Mathf.Max(transform.lossyScale.x, transform.lossyScale.z)
        : 0.3f;

    // 점프나 낙하 뒤 땅에 닿으면 표면 발소리 + 쿵 소리를 낸다. 세게 떨어질수록 크다
    private void UpdateLanding(float deltaTime)
    {
        float y = transform.position.y;
        float verticalSpeed = (y - _lastY) / deltaTime;
        _lastY = y;

        // 순간이동이나 넉다운(래그돌)은 착지로 치지 않는다
        if (Mathf.Abs(verticalSpeed * deltaTime) > teleportDistance || (_knockdown != null && _knockdown.IsDown))
        {
            _airborne = false;
            _fallSpeed = 0f;
            return;
        }

        if (!_airborne)
        {
            // 위아래로 움직일 때만 발밑을 확인한다 (평지를 걷는 동안은 레이를 쏘지 않는다)
            if (Mathf.Abs(verticalSpeed) > 1f && !FindGround(out _))
            {
                _airborne = true;
                _fallSpeed = 0f;
            }
            return;
        }

        if (verticalSpeed < 0f) _fallSpeed = Mathf.Max(_fallSpeed, -verticalSpeed);
        if (verticalSpeed > 0.5f || !FindGround(out RaycastHit ground)) return;   // 아직 올라가는 중이거나 공중

        _airborne = false;
        if (_fallSpeed >= minLandingSpeed) PlayLanding(ground, _fallSpeed);
        else PlaySoftLanding(ground);
        _fallSpeed = 0f;
    }

    private void PlayLanding(RaycastHit ground, float fallSpeed)
    {
        SoundManager sound = SoundManager.Instance;
        if (sound == null) return;

        float strength = Mathf.InverseLerp(minLandingSpeed, maxLandingSpeed, fallSpeed);
        float volume = Mathf.Lerp(0.6f, 1.5f, strength);
        if (_networkObject != null && _networkObject.IsOwner) volume *= localPlayerVolumeScale;

        SoundData surfaceSound = profile != null ? profile.GetSound(FootstepSurface.Resolve(ground.collider)) : null;
        if (surfaceSound != null) sound.PlaySFXAt(surfaceSound, ground.point, volume);
        if (landSound != null) sound.PlaySFXAt(landSound, ground.point, volume);
        PlayStepEffect(ground.point, ground.normal);

        // 착지하자마자 걸음 소리가 겹치지 않게 잠시 막는다
        _stepBlockedUntil = Time.time + landingStepDelay;
    }

    // 점프 꼭대기에서 가로대·턱에 얹히듯 올라서면 떨어지는 속도가 거의 없다. 쿵 소리 없이 밟은 표면 소리만 낸다
    private void PlaySoftLanding(RaycastHit ground)
    {
        SoundManager sound = SoundManager.Instance;
        SoundData surfaceSound = profile != null ? profile.GetSound(FootstepSurface.Resolve(ground.collider)) : null;
        if (sound == null || surfaceSound == null) return;

        // 걷는 속도와 상관없이 일정하게 (점프해 올라설 때는 수평 속도가 거의 없어 걸음 크기 배율이 최저로 깎인다)
        float volume = softLandingVolume;
        if (_networkObject != null && _networkObject.IsOwner) volume *= localPlayerVolumeScale;
        sound.PlaySFXAt(surfaceSound, ground.point, volume);
        _stepBlockedUntil = Time.time + landingStepDelay;
    }

    /// <summary>
    /// 밟은 자리에 걸음 이펙트를 띄운다. 풀에서 순서대로 꺼내 쓰며, 아직 재생 중인 것을 다시 쓰면 처음부터 다시 재생한다.
    /// </summary>
    private void PlayStepEffect(Vector3 point, Vector3 groundNormal)
    {
        if (stepEffect == null) return;
        if (_effectPool.Count == 0) CreateEffectPool();

        ParticleSystem effect = _effectPool[_nextEffectIndex];
        _nextEffectIndex = (_nextEffectIndex + 1) % _effectPool.Count;

        // 프리팹 원래 회전은 유지하고, 경사에서는 바닥 방향(법선)만큼 추가로 기울임.
        // 바닥 표면에 딱 붙으면 파티클 절반이 묻혀 보이므로 법선 방향으로 살짝 띄움
        Quaternion slope = Quaternion.FromToRotation(Vector3.up, groundNormal);
        effect.transform.SetPositionAndRotation(point + groundNormal * effectHeightOffset, slope * stepEffect.transform.rotation);
        effect.Clear(true);
        effect.Play(true);
    }

    private void CreateEffectPool()
    {
        _effectRoot = new GameObject($"{name}_FootstepEffects").transform;

        int size = Mathf.Max(1, effectPoolSize);
        for (int i = 0; i < size; i++)
        {
            ParticleSystem effect = Instantiate(stepEffect, _effectRoot);
            effect.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);   // Play On Awake로 생성 즉시 재생되는 것 방지
            _effectPool.Add(effect);
        }
    }

    // 루트(캡슐 중심)에서 발바닥까지의 거리
    private float FootOffset
    {
        get
        {
            if (_characterController == null) return 1f;
            return (_characterController.height * 0.5f - _characterController.center.y) * transform.lossyScale.y;
        }
    }

    private float CurrentVolumeScale
    {
        get
        {
            float speedScale = Mathf.Clamp(_smoothedSpeed / referenceSpeed, volumeScaleRange.x, volumeScaleRange.y);
            bool isLocalPlayer = _networkObject != null && _networkObject.IsOwner;
            return isLocalPlayer ? speedScale * localPlayerVolumeScale : speedScale;
        }
    }

    // 내 몸이나 플레이어 래그돌(Player에서 분리되어 있음)은 바닥이 아님
    private bool IsPlayerBody(Collider hit)
    {
        return hit.transform.IsChildOf(transform) || hit.GetComponentInParent<RagdollDriver>() != null;
    }
}
