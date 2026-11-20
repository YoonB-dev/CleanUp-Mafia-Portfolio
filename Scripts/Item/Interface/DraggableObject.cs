using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(NetworkObject))]
public class DraggableObject : NetworkBehaviour
{
    [Header("Drag Settings")]
    [SerializeField] private float followSpeed = 12f;
    [SerializeField] private float rotateSpeed = 10f;
    [SerializeField] private float followOffsetDistance = 1.5f; // 플레이어 정면으로부터의 거리
    [Tooltip("플레이어와 오브젝트(콜라이더 경계) 사이가 이 거리보다 벌어지면 서버가 잡기를 강제로 해제한다 (낭떠러지로 떨어짐 등)")]
    [SerializeField] private float releaseDistance = 3f;

    [Header("Ground Follow")]
    [Tooltip("바닥으로 인식할 레이어. 바닥/계단만 넣을 것 (플레이어나 아이템 레이어가 들어가면 그 위로 올라탄다). 비워두면 Floor")]
    [SerializeField] private LayerMask groundMask;
    [Tooltip("한 번에 올라갈 수 있는 최대 높이 (계단 한 칸). 이보다 높은 곳은 벽으로 보고 올라타지 않는다 (몸 전체가 파묻혔을 때만 위로 꺼낸다)")]
    [SerializeField] private float maxStepUp = 0.5f;
    [Tooltip("끄는 중 계단처럼 따라 내려가는 최대 깊이. 이보다 깊으면 낭떠러지로 보고 떨어진다")]
    [SerializeField] private float maxStepDown = 1.5f;
    [Tooltip("내려갈 때 높이를 따라가는 속도 (m/s). 올라갈 때는 땅에 파묻히지 않도록 바로 올라선다")]
    [SerializeField] private float verticalSpeed = 4f;

    [Header("Walls")]
    [Tooltip("끄는 동안 뚫고 지나가지 못하는 레이어 (벽, 가구 등). 바닥/계단은 넣지 말 것 (높이는 Ground Follow가 따로 맞춘다)")]
    [SerializeField] private LayerMask wallMask = (1 << 0) | (1 << 8); // Default, Wall

    [Header("Falling")]
    [Tooltip("공중에서 놓거나 낭떠러지에서 떨어질 때의 낙하 가속도 (m/s²)")]
    [SerializeField] private float fallGravity = 20f;
    [Tooltip("놓은 뒤 이 거리만큼 떨어져도 바닥이 없으면 낙하를 멈춘다 (바닥 레이어 누락 시 끝없이 떨어지는 것 방지)")]
    [SerializeField] private float maxFallDistance = 30f;

    [Header("Grip Settings")]
    [Tooltip("플레이어 손이 잡아야 할 위치 (없으면 이 오브젝트의 Transform 사용)")]
    [SerializeField] private Transform handlePoint;

    [Header("Sound")]
    [Tooltip("잡을 때 나는 소리 (SO/Sound/SFX/Item/PickupSound). 비워 두면 소리 없음")]
    [SerializeField] private SoundData grabSound;
    [Tooltip("끄는 동안 반복되는 바퀴 구르는 소리. 빠를수록 크고 높아진다")]
    [SerializeField] private SoundData rollSound;
    [Tooltip("이 속도(m/s)에서 바퀴 소리가 가장 크다")]
    [SerializeField] private float fullSoundSpeed = 3f;
    [Tooltip("바라볼 때 안내에 보이는 이름의 InGame 다국어 키")]
    [SerializeField] private string nameKey;
    public Transform HandlePoint => handlePoint != null ? handlePoint : transform;
    public string LocalizedName => InteractPromptUI.Translate(nameKey);

    private Rigidbody _rb;
    private PickupHighlight _pickupHighlight;
    private NetworkVariable<ulong> _grabberPlayerId = new NetworkVariable<ulong>(
        ulong.MaxValue,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    public bool IsBeingDragged => _grabberPlayerId.Value != ulong.MaxValue;
    public ulong GrabberPlayerId => _grabberPlayerId.Value;

    private Transform _currentGrabberTransform;
    private RagdollPoser _currentGrabberRagdollPoser;

    // 바닥 감지용 (끌기 시작할 때 계산)
    private Collider[] _bodyColliders = System.Array.Empty<Collider>(); // 트리거 제외 콜라이더 (거리 판정에도 사용)
    private float _bottomOffset;  // 피벗에서 콜라이더 바닥면까지 높이
    private float _bodyHeight;    // 콜라이더 전체 높이. 감지 시작 높이로 사용
    private Vector3[] _footprintPoints = { Vector3.zero }; // 바닥면 전체를 덮는 감지 지점 (Y축 회전만 기준으로 한 로컬 좌표, y는 0)
    private readonly RaycastHit[] _groundHits = new RaycastHit[8];

    // 낙하 (Kinematic이라 중력이 없으므로 직접 떨어뜨린다)
    private float _fallSpeed;
    private bool _isSettling;     // 놓은 뒤 바닥에 닿을 때까지 떨어지는 중
    private float _settleStartY;

    // 멀리서 잡으면 끌려오기 전부터 거리 초과로 놓이지 않도록, 한 번 가까이 온 뒤부터 거리 판정을 한다
    private bool _hasReachedGrabber;

    // 끄는 소리. 위치는 모두에게 복제되므로 각 클라이언트가 직접 속도를 재서 낸다
    private AudioSource _rollSource;
    private Vector3 _lastSoundPosition;
    private float _smoothedSpeed;
    private const float ROLL_FADE_SPEED = 6f;      // 볼륨이 목표값을 따라가는 빠르기
    private const float MIN_SOUND_SPEED = 0.15f;   // 이보다 느리면 멈춘 것으로 본다

    private const float MAX_GROUND_SLOPE = 40f;   // 이보다 가파른 면은 경사로 보지 않는다 (도)
    private const float FLAT_NORMAL_Y = 0.996f;   // 이보다 평평한 면(턱 윗면, 단상)은 경사 여유를 주지 않는다 (약 5도)
    private const float WALL_SKIN = 0.03f;        // 벽과 이만큼 띄워 멈춘다 (딱 붙으면 다음 쓸기가 시작부터 겹쳐 벽을 놓친다)
    private const float MAX_WALL_NORMAL_Y = 0.7f; // 면 방향의 위아래 성분이 이보다 크면 바닥/천장으로 보고 막지 않는다
    private readonly Collider[] _wallOverlaps = new Collider[16];

    // 바닥 감지 지점 간격. 이보다 좁은 턱은 지점 사이로 빠질 수 있다
    private const float FOOTPRINT_SAMPLE_SPACING = 0.5f;
    private const int MAX_SAMPLES_PER_AXIS = 6;
    // 바닥면 가장자리에서 안쪽으로 들이는 거리. 바로 옆에 붙은 턱까지 밟고 올라서지 않도록
    private const float FOOTPRINT_INSET = 0.05f;
    // 감지 레이는 오브젝트 꼭대기보다 이만큼 위에서 쏜다
    private const float RAY_START_MARGIN = 0.1f;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _pickupHighlight = GetComponent<PickupHighlight>();
        _lastSoundPosition = transform.position;

        if (groundMask == 0)
        {
            groundMask = LayerMask.GetMask("Floor");
        }
    }

    public override void OnNetworkSpawn()
    {
        _grabberPlayerId.OnValueChanged += OnGrabberChanged;
    }

    public override void OnNetworkDespawn()
    {
        _grabberPlayerId.OnValueChanged -= OnGrabberChanged;
    }

    public void SetHighlighted(bool highlighted)
    {
        // 잡혀있는 상태라면 강제로 아웃라인을 끔
        if (IsBeingDragged && highlighted)
        {
            highlighted = false;
        }

        if (_pickupHighlight != null)
        {
            _pickupHighlight.SetHighlighted(highlighted);
        }
    }

    private void OnGrabberChanged(ulong previous, ulong current)
    {
        if (current == ulong.MaxValue)
        {
            // 서버가 강제로 놓게 했을 수도 있으므로, 잡고 있던 플레이어 쪽 끌기 상태도 정리시킨다
            if (_currentGrabberTransform != null && _currentGrabberTransform.TryGetComponent<PlayerInteraction>(out var previousInteraction))
            {
                previousInteraction.OnDragReleased(this);
            }

            // 잡기 해제 시 래그돌 포즈 초기화
            if (_currentGrabberRagdollPoser != null)
            {
                _currentGrabberRagdollPoser.SetCarryRequested(false);
                _currentGrabberRagdollPoser.SetCarryTarget(null);
            }
            _currentGrabberTransform = null;
            _currentGrabberRagdollPoser = null;
        }
        else if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(current, out var netObj))
        {
            SetHighlighted(false);

            // 잡는 순간. 잡은 사람 값이 모든 클라이언트에 복제되므로 각자 여기서 소리를 낸다
            if (previous == ulong.MaxValue && grabSound != null && SoundManager.Instance != null)
            {
                SoundManager.Instance.PlaySFXAt(grabSound, transform.position);
            }

            _currentGrabberTransform = netObj.transform;
            if (netObj.TryGetComponent<PlayerInteraction>(out var interaction))
            {
                _currentGrabberRagdollPoser = interaction.playerRagDollPoser;

                // [1] 손 뻗기 자세 유도 (RagdollPoser 활용)
                _currentGrabberRagdollPoser?.SetCarryRequested(true);
                if (TryGetComponent<CarryGripPoints>(out var gripPoints))
                {
                    _currentGrabberRagdollPoser?.SetCarryTarget(gripPoints);
                }
            }
        }
    }

    /// <summary>
    /// 실제 서버에서 끌기 시작을 처리하는 로직 (RPC가 아니므로 서버 내부에서 자유롭게 호출 가능).
    /// 호출하는 쪽(PlayerInteraction)이 자신이 소유한 오브젝트 위에서 ServerRpc를 받아 이 메서드를 호출해야 한다.
    /// </summary>
    public void StartDrag(ulong playerId)
    {
        if (!IsServer || IsBeingDragged) return;
        CacheFootprint();
        _hasReachedGrabber = false;
        _isSettling = false;
        _fallSpeed = 0f;
        _grabberPlayerId.Value = playerId;
    }

    /// <summary>
    /// 실제 서버에서 끌기 해제를 처리하는 로직 (RPC가 아니므로 서버 내부에서 자유롭게 호출 가능).
    /// </summary>
    public void StopDrag()
    {
        if (!IsServer) return;

        if (IsBeingDragged)
        {
            // 공중에서 놓았을 수 있으므로 바닥에 닿을 때까지 떨어뜨린다
            _isSettling = true;
            _fallSpeed = 0f;
            _settleStartY = _rb.position.y;
        }
        _grabberPlayerId.Value = ulong.MaxValue;
    }

    private void Update()
    {
        UpdateDragSound();
    }

    // 끄는 동안 바퀴 소리를 속도에 맞춰 키운다. 놓거나 멈추면 부드럽게 줄어든다
    private void UpdateDragSound()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        Vector3 position = transform.position;
        Vector3 delta = position - _lastSoundPosition;
        _lastSoundPosition = position;
        delta.y = 0f;
        float speed = delta.magnitude / dt;
        if (speed > 20f) speed = 0f;   // 순간이동(스폰, 위치 보정)은 소리로 치지 않는다
        _smoothedSpeed = Mathf.Lerp(_smoothedSpeed, speed, 1f - Mathf.Exp(-10f * dt));

        float amount = IsSpawned && IsBeingDragged && _smoothedSpeed > MIN_SOUND_SPEED
            ? Mathf.Clamp01(_smoothedSpeed / Mathf.Max(0.01f, fullSoundSpeed))
            : 0f;

        if (rollSound != null && (amount > 0f || _rollSource != null))
        {
            if (_rollSource == null && SoundManager.Instance != null) _rollSource = SoundManager.Instance.CreateLoopSource(rollSound, transform);
            if (_rollSource != null)
            {
                float target = rollSound.Volume * Mathf.Sqrt(amount);
                _rollSource.volume = Mathf.MoveTowards(_rollSource.volume, target, ROLL_FADE_SPEED * rollSound.Volume * dt);
                _rollSource.pitch = Mathf.Lerp(0.8f, 1.25f, amount);
                if (_rollSource.volume > 0.001f && !_rollSource.isPlaying) _rollSource.Play();
                else if (_rollSource.volume <= 0.001f && _rollSource.isPlaying) _rollSource.Stop();
            }
        }
    }

    private void FixedUpdate()
    {
        if (!IsServer) return;

        if (!IsBeingDragged)
        {
            if (_isSettling) SettleStep();
            return;
        }

        // 끌던 플레이어가 나가서 사라졌으면 놓는다 (안 그러면 아무도 다시 잡을 수 없는 상태로 남는다)
        if (_currentGrabberTransform == null)
        {
            StopDrag();
            return;
        }

        // 떨어지거나 끼어서 플레이어와 너무 멀어지면 서버가 잡기를 해제한다 (이후 착지 처리로 이어짐)
        if (!IsTooFarFromGrabber())
        {
            _hasReachedGrabber = true;
        }
        else if (_hasReachedGrabber)
        {
            StopDrag();
            return;
        }

        // [2] 플레이어 정면 방향으로 Target Position 계산 (회전 시 오브젝트가 호를 그리며 따라옴)
        Vector3 targetPosition = _currentGrabberTransform.position + (_currentGrabberTransform.forward * followOffsetDistance);
        targetPosition.y = _rb.position.y; // 높이는 플레이어가 아니라 발밑 바닥 기준으로 따로 계산 (점프해도 안 딸려오게)

        // [3] 캐릭터 회전값 그대로 적용 (플레이어 바라보는 방향 동기화)
        Quaternion targetRotation = Quaternion.LookRotation(_currentGrabberTransform.forward, Vector3.up);
        Quaternion newRot = Quaternion.Slerp(_rb.rotation, targetRotation, Time.fixedDeltaTime * rotateSpeed);

        // 물리 위치 이동. 높이는 이번 프레임에 돌아간 바닥면 기준으로 구한다
        Vector3 newPos = Vector3.Lerp(_rb.position, targetPosition, Time.fixedDeltaTime * followSpeed);
        // 키네마틱이라 MovePosition은 벽을 무시한다. 벽에 막히면 그 앞까지만 가고 벽을 따라 미끄러진다
        newPos = BlockByWalls(newPos);
        newPos.y = GetGroundFollowHeight(newPos, newRot);
        // 돌다가 긴 몸이 벽에 들어가면 밖으로 밀어낸다
        newPos += PushOutOfWalls(newPos, newRot);
        _rb.MovePosition(newPos);
        _rb.MoveRotation(newRot);
    }

    /// <summary>
    /// 경사면 위 지점이 몸 가운데보다 높아도 되는 만큼 = 가운데에서의 수평 거리 × 그 면의 기울기.
    /// 평평한 면(턱·단상 윗면)과 너무 가파른 면은 0이라 지금처럼 maxStepUp까지만 올라선다
    /// </summary>
    private static float SlopeAllowance(Vector3 normal, Vector3 localPoint)
    {
        float ny = normal.y;
        if (ny >= FLAT_NORMAL_Y || ny < Mathf.Cos(MAX_GROUND_SLOPE * Mathf.Deg2Rad)) return 0f;

        float tan = Mathf.Sqrt(1f - ny * ny) / ny;
        return new Vector2(localPoint.x, localPoint.z).magnitude * tan;
    }

    /// <summary>
    /// 이번에 수평으로 움직일 만큼 몸을 쓸어 보고, 벽에 닿으면 그 앞까지만 간 뒤 남은 만큼은 벽면을 따라 미끄러진다. [서버 전용]
    /// </summary>
    private Vector3 BlockByWalls(Vector3 newPos)
    {
        Vector3 start = _rb.position;
        Vector3 move = newPos - start;
        move.y = 0f;

        Vector3 moved = Vector3.zero;
        for (int i = 0; i < 2 && move.sqrMagnitude > 1e-6f; i++)
        {
            float length = move.magnitude;
            Vector3 direction = move / length;
            if (!TryFindWall(direction, length + WALL_SKIN, out RaycastHit hit))
            {
                moved += move;
                break;
            }

            float allowed = Mathf.Max(0f, hit.distance - WALL_SKIN);
            moved += direction * allowed;
            // 남은 이동을 벽면 방향으로 돌려 한 번 더 시도한다 (벽에 비스듬히 끌면 벽을 따라 미끄러지게)
            Vector3 normal = hit.normal;
            normal.y = 0f;
            move = Vector3.ProjectOnPlane(direction * (length - allowed), normal.normalized);
            move.y = 0f;
            _rb.position = start + moved; // 다음 쓸기는 막힌 자리에서 시작한다
        }
        _rb.position = start;

        return new Vector3(start.x + moved.x, newPos.y, start.z + moved.z);
    }

    /// <summary>몸을 direction으로 distance만큼 쓸었을 때 처음 닿는 벽. 바닥에 가까운 면·사람·굴러다니는 물건은 무시한다</summary>
    private bool TryFindWall(Vector3 direction, float distance, out RaycastHit wall)
    {
        wall = default;
        float nearest = float.PositiveInfinity;
        foreach (RaycastHit hit in _rb.SweepTestAll(direction, distance, QueryTriggerInteraction.Ignore))
        {
            // 시작부터 겹친 것은 방향을 믿을 수 없어 건너뛴다 (파고든 건 PushOutOfWalls가 밀어낸다). 안 그러면 벽에서 떨어지는 쪽으로도 못 움직인다
            if (hit.distance <= 0f || hit.distance >= nearest || !IsWall(hit.collider)) continue;
            if (Mathf.Abs(hit.normal.y) > MAX_WALL_NORMAL_Y) continue; // 바닥·턱 윗면
            nearest = hit.distance;
            wall = hit;
        }
        return !float.IsPositiveInfinity(nearest);
    }

    /// <summary>
    /// 새 위치·회전에서 몸이 벽에 파고들었으면 빠져나올 만큼(수평)을 돌려준다. [서버 전용]
    /// 자식 콜라이더는 지금 몸 기준 상대 위치를 새 자세로 옮겨서 잰다
    /// </summary>
    private Vector3 PushOutOfWalls(Vector3 newPos, Quaternion newRot)
    {
        Vector3 push = Vector3.zero;
        Quaternion toLocal = Quaternion.Inverse(_rb.rotation);
        foreach (Collider body in _bodyColliders)
        {
            if (body == null) continue;
            Vector3 bodyPos = newPos + newRot * (toLocal * (body.transform.position - _rb.position)) + push;
            Quaternion bodyRot = newRot * (toLocal * body.transform.rotation);
            Bounds bounds = body.bounds;

            int count = Physics.OverlapBoxNonAlloc(bodyPos + (bounds.center - body.transform.position), bounds.extents + Vector3.one * bounds.extents.magnitude,
                _wallOverlaps, Quaternion.identity, wallMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                Collider other = _wallOverlaps[i];
                if (!IsWall(other)) continue;
                if (!Physics.ComputePenetration(body, bodyPos, bodyRot, other, other.transform.position, other.transform.rotation,
                        out Vector3 direction, out float depth)) continue;
                if (Mathf.Abs(direction.y) > MAX_WALL_NORMAL_Y) continue;

                direction.y = 0f;
                Vector3 step = direction.normalized * depth;
                push += step;
                bodyPos += step;
            }
        }
        return push;
    }

    /// <summary>끄는 물건을 막는 것인지. 자기 몸, 사람(래그돌 포함), 굴러다니는 물건은 막지 않는다</summary>
    private bool IsWall(Collider other)
    {
        if (other == null || ((1 << other.gameObject.layer) & wallMask) == 0) return false;
        if (other.attachedRigidbody == _rb) return false;
        if (other.attachedRigidbody != null && !other.attachedRigidbody.isKinematic) return false;
        if (other.GetComponentInParent<RagdollDriver>() != null || other.GetComponentInParent<PlayerInteraction>() != null) return false;
        return true;
    }

    /// <summary>
    /// 콜라이더 전체 범위로 바닥면 높이와 바닥 감지 지점을 계산한다. 끄는 동안은 Y축 회전만 하므로 시작할 때 한 번이면 충분 <br/>
    /// 감지 지점은 Y축 회전 기준 로컬 좌표로 저장해서, 끄는 중 방향이 바뀌어도 바닥면 전체(앞뒤 끝 포함)를 덮는다
    /// </summary>
    private void CacheFootprint()
    {
        _bodyColliders = System.Array.FindAll(GetComponentsInChildren<Collider>(), col => !col.isTrigger && col.enabled);

        Quaternion toYawLocal = Quaternion.Inverse(GetYawRotation(_rb.rotation));
        bool hasBounds = false;
        Bounds bounds = default; // Y축 회전 기준 로컬 좌표
        foreach (Collider col in _bodyColliders)
        {
            // BoxCollider는 실제 상자 꼭짓점을 쓰고, 그 외에는 월드 AABB 꼭짓점으로 근사한다
            BoxCollider box = col as BoxCollider;
            Vector3 center = box != null ? box.center : col.bounds.center;
            Vector3 extents = box != null ? box.size * 0.5f : col.bounds.extents;

            for (int i = 0; i < 8; i++)
            {
                Vector3 sign = new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f);
                Vector3 corner = center + Vector3.Scale(extents, sign);
                if (box != null)
                {
                    corner = box.transform.TransformPoint(corner);
                }
                Vector3 localCorner = toYawLocal * (corner - _rb.position);

                if (!hasBounds)
                {
                    bounds = new Bounds(localCorner, Vector3.zero);
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(localCorner);
                }
            }
        }

        if (!hasBounds)
        {
            _bottomOffset = 0f;
            _bodyHeight = 0f;
            _footprintPoints = new[] { Vector3.zero };
            return;
        }

        _bottomOffset = -bounds.min.y;
        _bodyHeight = bounds.size.y;

        // 바닥면을 격자로 나눠 감지 지점을 만든다. 가운데만 보면 긴 오브젝트의 앞뒤 끝이 땅에 파묻힌다
        float[] xs = GetFootprintAxisSamples(bounds.min.x, bounds.max.x);
        float[] zs = GetFootprintAxisSamples(bounds.min.z, bounds.max.z);
        _footprintPoints = new Vector3[xs.Length * zs.Length];
        for (int x = 0; x < xs.Length; x++)
        {
            for (int z = 0; z < zs.Length; z++)
            {
                _footprintPoints[x * zs.Length + z] = new Vector3(xs[x], 0f, zs[z]);
            }
        }
    }

    /// <summary>
    /// min~max 구간(양 끝은 살짝 안쪽)을 FOOTPRINT_SAMPLE_SPACING 이하 간격으로 나눈 지점들
    /// </summary>
    private static float[] GetFootprintAxisSamples(float min, float max)
    {
        float inset = Mathf.Min(FOOTPRINT_INSET, (max - min) * 0.5f);
        min += inset;
        max -= inset;

        int count = Mathf.Clamp(Mathf.CeilToInt((max - min) / FOOTPRINT_SAMPLE_SPACING) + 1, 1, MAX_SAMPLES_PER_AXIS);
        float[] samples = new float[count];
        for (int i = 0; i < count; i++)
        {
            samples[i] = count == 1 ? (min + max) * 0.5f : Mathf.Lerp(min, max, (float)i / (count - 1));
        }
        return samples;
    }

    private static Quaternion GetYawRotation(Quaternion rotation)
    {
        return Quaternion.Euler(0f, rotation.eulerAngles.y, 0f);
    }

    /// <summary>
    /// 잡고 있는 플레이어와 오브젝트 사이가 releaseDistance보다 벌어졌는지. [서버 전용] <br/>
    /// 큰 오브젝트라 중심점이 아니라 콜라이더 경계 기준으로 잰다 (잡기 시작 거리 검증과 같은 방식)
    /// </summary>
    private bool IsTooFarFromGrabber()
    {
        Vector3 grabberPosition = _currentGrabberTransform.position;

        if (_bodyColliders.Length == 0)
        {
            return Vector3.Distance(grabberPosition, _rb.position) > releaseDistance;
        }

        foreach (Collider col in _bodyColliders)
        {
            if (col != null && Vector3.Distance(grabberPosition, col.bounds.ClosestPoint(grabberPosition)) <= releaseDistance)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 끄는 중 이동할 위치의 높이를 계산한다. [서버 전용] <br/>
    /// 계단 범위(maxStepDown) 안에 바닥이 있으면 따라가고, 없으면 낭떠러지로 보고 떨어진다
    /// </summary>
    private float GetGroundFollowHeight(Vector3 position, Quaternion rotation)
    {
        float currentY = _rb.position.y;

        if (TryFindGroundHeight(position, rotation, maxStepDown, out float groundY))
        {
            _fallSpeed = 0f;
            // 바닥보다 낮으면 바로 올라선다. 천천히 올라가면 땅에 파묻히고, 계단에서는 뒤처지다 계단을 놓쳐 떨어진다
            return groundY >= currentY
                ? groundY
                : Mathf.MoveTowards(currentY, groundY, verticalSpeed * Time.fixedDeltaTime);
        }

        return currentY - NextFallStep();
    }

    /// <summary>
    /// 놓은 뒤 한 물리 프레임만큼 떨어뜨리고, 바닥에 닿으면 그 위에 세운 뒤 낙하를 끝낸다. [서버 전용]
    /// </summary>
    private void SettleStep()
    {
        Vector3 position = _rb.position;
        float fallStep = NextFallStep();
        bool landed = false;

        if (TryFindGroundHeight(position, _rb.rotation, fallStep, out float groundY))
        {
            // 이번 프레임 안에 닿는 바닥이면 그 위에 세운다 (땅에 파묻힌 채 놓았으면 위로 꺼낸다)
            position.y = groundY;
            landed = true;
        }
        else
        {
            position.y -= fallStep;
        }

        _rb.MovePosition(position);

        if (landed || _settleStartY - position.y > maxFallDistance)
        {
            _isSettling = false;
            _fallSpeed = 0f;
        }
    }

    /// <summary>
    /// 낙하 속도를 중력만큼 키우고 이번 물리 프레임에 떨어질 거리를 돌려준다
    /// </summary>
    private float NextFallStep()
    {
        _fallSpeed += fallGravity * Time.fixedDeltaTime;
        return _fallSpeed * Time.fixedDeltaTime;
    }

    /// <summary>
    /// 해당 위치에서 바닥면 전체 아래의 바닥을 찾아, 오브젝트가 그 위에 올라섰을 때의 피벗 높이를 구한다. <br/>
    /// 바닥면 격자 지점마다 오브젝트 꼭대기 위에서 레이를 쏴서, 현재 바닥면 + maxStepUp 이하인 바닥 중 가장 높은 것을 고른다
    /// (한쪽 끝만 걸쳐도 그 위로 올라서고, 그보다 높은 벽/단상은 무시). <br/>
    /// 계단은 MeshCollider 하나라 캐스트 한 번에 맞는 지점이 하나뿐이다. 그래서 구 하나로 훑으면 가장 높은 칸만 보고
    /// 너무 높다며 계단 전체를 놓쳤는데, 지점마다 레이를 쏘면 각 지점 바로 아래 칸 높이를 따로 얻는다. <br/>
    /// 올라설 바닥이 하나도 없는데 몸이 바닥에 파묻혀 있으면 (빠르게 돌려 계단 속으로 들어간 경우 등) 그 위로 꺼낸다
    /// </summary>
    private bool TryFindGroundHeight(Vector3 position, Quaternion rotation, float downDistance, out float pivotY)
    {
        pivotY = 0f;
        float bottomY = _rb.position.y - _bottomOffset;
        float maxGroundY = bottomY + maxStepUp;

        // 경사면 여유만큼 높은 땅도 위에서 맞혀야 하므로 그만큼 더 위에서 쏜다 (땅속에서 쏘면 그 면을 못 맞힌다)
        float reach = 0f;
        foreach (Vector3 point in _footprintPoints) reach = Mathf.Max(reach, new Vector2(point.x, point.z).magnitude);
        float slopeRise = reach * Mathf.Tan(MAX_GROUND_SLOPE * Mathf.Deg2Rad);
        float rayStartY = bottomY + Mathf.Max(maxStepUp + slopeRise, _bodyHeight) + RAY_START_MARGIN;
        float rayDistance = rayStartY - bottomY + downDistance;
        Quaternion yaw = GetYawRotation(rotation);

        float bestGroundY = float.NegativeInfinity;   // 올라설 수 있는 바닥 중 가장 높은 곳
        float buriedGroundY = float.NegativeInfinity; // 한 칸보다 높아서 몸이 파묻힌 바닥 중 가장 높은 곳
        foreach (Vector3 point in _footprintPoints)
        {
            Vector3 origin = position + yaw * point;
            origin.y = rayStartY;

            int count = Physics.RaycastNonAlloc(origin, Vector3.down, _groundHits, rayDistance, groundMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = _groundHits[i];
                if (hit.rigidbody == _rb) continue;

                // 경사면이면 몸 가운데에서 이 지점까지 거리만큼 더 높아도 바닥이다.
                // 안 그러면 언덕에서 돌려 긴 쪽이 오르막을 향할 때 그쪽 땅을 벽으로 보고 무시해서 몸이 땅에 박힌다
                if (hit.point.y > maxGroundY + SlopeAllowance(hit.normal, point))
                {
                    buriedGroundY = Mathf.Max(buriedGroundY, hit.point.y);
                }
                else
                {
                    bestGroundY = Mathf.Max(bestGroundY, hit.point.y);
                }
            }
        }

        float groundY = float.IsNegativeInfinity(bestGroundY) ? buriedGroundY : bestGroundY;
        if (float.IsNegativeInfinity(groundY))
        {
            return false;
        }

        pivotY = groundY + _bottomOffset;
        return true;
    }
}