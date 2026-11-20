using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 바닥에 놓여있는 청소 도구 프리펩에 부착하는 스크립트.
/// PickupItem에 의해 들려있는 상태에서만 발사(청소) 입력을 감지하고 작동한다.
/// </summary>
[RequireComponent(typeof(PickupItem))]
public class PaintCleaner : NetworkBehaviour, IUsableItem
{
    [Header("Settings")]
    [SerializeField] private float cleanRange = 3f;
    [SerializeField] private float cleanRate = 0.1f;
    [Tooltip("지우개 반경 (미터, 월드 기준). 어떤 표면이든 같은 실제 크기로 지워진다")]
    [SerializeField] private float brushRadius = 0.3f;
    [SerializeField] private LayerMask paintableLayers;

    // 서버 사거리 검증 여유. 요청이 도착하는 동안 플레이어가 움직인 거리만큼 봐준다
    private const float RANGE_TOLERANCE = 1f;

    [Header("VFX")]
    [Tooltip("청소 지점에 젖은 자국을 남기는 이펙트 프리팹 (Broom_Clean_FX_Final)")]
    [SerializeField] private BroomWetTrail wetTrailPrefab;
    [Tooltip("이 시간(초) 동안 청소 지점이 들어오지 않으면 자국 접촉을 끊는다. 네트워크 지연보다 넉넉해야 자국이 중간에 끊기지 않는다")]
    [SerializeField] private float wetContactTimeout = 0.3f;

    [Header("Sound")]
    [Tooltip("닦는 동안 반복 재생할 소리 (SO/Sound/SFX/Item/MopSound)")]
    [SerializeField] private SoundData mopSound;
    [Tooltip("닦는 소리가 켜지고 꺼지는 페이드 시간(초)")]
    [SerializeField] private float mopSoundFade = 0.12f;

    private PickupItem _pickupItem;
    private PlayerInteraction _localPlayer; // 현재 나를 들고 있는 주인
    private Camera _playerCamera;
    private BroomWetTrail _wetTrail;
    private AudioSource _mopAudio;

    private float _nextCleanTime;
    private float _lastWetContactTime = float.NegativeInfinity;
    private bool _isCleaning;

    /// <summary>지금 실제로 닦이고 있는지 (청소 지점이 계속 들어오는 중). 모든 클라이언트에서 같다</summary>
    public bool IsCleaningContact => Time.time - _lastWetContactTime <= wetContactTimeout;
    /// <summary>마지막으로 닦인 지점 (월드)</summary>
    public Vector3 LastCleanPoint { get; private set; }
    /// <summary>마지막으로 닦인 면의 노멀</summary>
    public Vector3 LastCleanNormal { get; private set; } = Vector3.up;

    private void Awake()
    {
        _pickupItem = GetComponent<PickupItem>();

        // 자국은 월드 공간에 남으므로 위치는 상관없고, 아이템과 수명만 같이 가도록 자식으로 둔다
        if (wetTrailPrefab != null) _wetTrail = Instantiate(wetTrailPrefab, transform);

        // 닦는 소리는 실제로 닦이는 지점에서 나도록 RPC마다 위치를 옮긴다
        if (mopSound != null && SoundManager.Instance != null) _mopAudio = SoundManager.Instance.CreateLoopSource(mopSound, transform);
    }

    private void Update()
    {
        if (!IsSpawned) return;

        // 청소 지점이 끊기면 자국 접촉 종료 (오너가 아닌 클라이언트도 처리해야 하므로 오너 체크보다 먼저)
        bool cleaningContact = IsCleaningContact;
        if (_wetTrail != null && !cleaningContact) _wetTrail.EndContact();
        UpdateMopSound(cleaningContact);

        // 1. 아무도 들고 있지 않으면 청소 로직 정지 (IsHeld는 PickupItem이 서버 권위로 관리)
        if (!_pickupItem.IsHeld)
        {
            _isCleaning = false;
            return;
        }

        // 2. 실소유주(오너)의 화면에서만 입력/레이캐스트 처리
        if (!IsOwner) return;

        // 3. 오너 = 곧 holder이므로, 로컬 플레이어를 그냥 나 자신으로 참조
        if (_localPlayer == null && NetworkManager.Singleton != null && NetworkManager.Singleton.LocalClient != null)
        {
            var localPlayerObj = NetworkManager.Singleton.LocalClient.PlayerObject;
            if (localPlayerObj != null)
            {
                _localPlayer = localPlayerObj.GetComponent<PlayerInteraction>();
            }
        }
        if (_localPlayer == null) return;

        // 4. 청소
        if (!_isCleaning) return;
        if (Time.time < _nextCleanTime) return;
        _nextCleanTime = Time.time + cleanRate;
        CleanPaint();
    }

    /// <summary>
    /// 좌클릭 사용 입력. 누르고 있는 동안 청소한다 (PlayerInteraction.OnUseItem에서 호출)
    /// </summary>
    public void OnUse(InputAction.CallbackContext context, Camera playerCamera)
    {
        if (context.performed) _isCleaning = true;
        else if (context.canceled) _isCleaning = false;
    }

    private void CleanPaint()
    {
        // 캐싱이 안 되어있다면 주인의 카메라를 찾아옴
        if (_playerCamera == null && _localPlayer != null)
        {
            _playerCamera = _localPlayer.GetComponentInChildren<Camera>(true);
        }
        if (_playerCamera == null) return;
        Ray ray = _playerCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0));
        if (!Physics.Raycast(ray, out RaycastHit hit, cleanRange, paintableLayers)) return;
        
        var paintable = hit.collider.GetComponent<PaintableSurface>();
        if (paintable == null) return;

        // 브러시는 월드 좌표 기준으로 그리므로 맞은 지점(월드)을 보낸다. 반경은 서버가 정한다
        RequestCleanServerRpc(paintable.SurfaceId, hit.point, hit.normal);
    }

    [ServerRpc]
    private void RequestCleanServerRpc(int surfaceId, Vector3 point, Vector3 normal, ServerRpcParams rpcParams = default)
    {
        if (!IsServer) return;

        // RPC를 보낸 클라이언트가 진짜 이 아이템을 들고 있는 주인인지 체크
        var holder = _pickupItem.Holder;
        if (holder == null || holder.OwnerClientId != rpcParams.Receive.SenderClientId)
        {
            Debug.LogWarning("[검증 거부] 아이템을 들고 있지 않은 클라이언트가 청소를 요청함.");
            return;
        }
        if (holder.Inventory != null && holder.Inventory.CurrentSlot == 4) return;

        // 클라이언트가 보낸 지점이 사거리 안인지 서버 시점에서 다시 확인
        Vector3 origin = holder.PlayerCamera != null ? holder.PlayerCamera.transform.position : holder.transform.position;
        if (Vector3.Distance(origin, point) > cleanRange + RANGE_TOLERANCE) return;

        // 반경은 클라이언트 값을 받지 않고 서버 설정값을 쓴다
        ApplyCleanClientRpc(surfaceId, point, normal, brushRadius);
    }

    [ClientRpc]
    private void ApplyCleanClientRpc(int surfaceId, Vector3 point, Vector3 normal, float radius)
    {
        PaintSurfaceManager.Instance.DrawAt(surfaceId, point, radius, isPaint: false);

        // 실제로 지워진 지점에 젖은 자국을 남긴다. 모든 클라이언트가 같은 지점을 받으므로 따로 동기화하지 않는다
        _lastWetContactTime = Time.time;
        LastCleanPoint = point;
        LastCleanNormal = normal;
        if (_wetTrail != null) _wetTrail.SetContact(point, normal);
        if (_mopAudio != null) _mopAudio.transform.position = point;
    }

    /// <summary>
    /// 젖은 자국과 같은 조건(청소 지점이 계속 들어오는 동안)으로 닦는 소리를 켜고 끈다. 뚝 끊기지 않게 짧게 페이드한다
    /// </summary>
    private void UpdateMopSound(bool cleaning)
    {
        if (_mopAudio == null || !_mopAudio.isActiveAndEnabled) return;

        if (cleaning && !_mopAudio.isPlaying)
        {
            _mopAudio.pitch = mopSound.GetRandomPitch();
            _mopAudio.Play();
        }

        float target = cleaning ? mopSound.Volume : 0f;
        float step = mopSound.Volume * Time.deltaTime / Mathf.Max(0.01f, mopSoundFade);
        _mopAudio.volume = Mathf.MoveTowards(_mopAudio.volume, target, step);

        if (!cleaning && _mopAudio.volume <= 0f && _mopAudio.isPlaying) _mopAudio.Stop();
    }
}