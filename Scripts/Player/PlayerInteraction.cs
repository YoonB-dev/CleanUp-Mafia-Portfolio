using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerInteraction : NetworkBehaviour
{
    [Header("Interaction Settings")]
    [SerializeField] private Camera playerCamera;
    public Camera PlayerCamera => playerCamera; // 외부에서 접근 가능하도록 프로퍼티 제공
    [SerializeField] private float interactDistance = 5f;
    [SerializeField] private float pickupRadius = 0.2f;    // 줍기 판정 구체 반지름 (작은 아이템 보정용)
    [SerializeField] private LayerMask pickupLayerMask = ~0;
    [Tooltip("카메라와 아이템 사이를 가리면 줍지 못하게 막는 레이어 (벽, 바닥 등). 비워두면 Default/Ground/Wall/Floor/Stair")]
    [SerializeField] private LayerMask pickupObstacleMask;

    private BoxPlacementPreview _placementPreview; // 박스 배치 프리뷰를 관리하는 컴포넌트(스크립트)
    private PlayerInventory _inventory; // 플레이어의 인벤토리 인터페이스
    public PlayerInventory Inventory => _inventory; // 외부에서 인벤토리 접근용
    private PickupItem hoveredItem; // 플레이어가 현재 바라보고 있는 아이템
    private DraggableObject hoveredDraggable; // 플레이어가 현재 바라보고 있는 드래그 가능한 오브젝트
    private Component hoveredButton; // 플레이어가 현재 바라보고 있는 투표 버튼이나 차단기

    // 바라보는 물체 안내. 대상이나 남은 시간이 바뀔 때만 문구를 다시 만든다
    private const string PICKUP_ACTION = "Player/Interaction_PickItem";
    private const string BUTTON_ACTION = "Player/Interaction-lignt";
    private const int PLACE_VARIANT = -1; // 같은 상자라도 줍기와 배치 안내를 구분한다
    private Object _promptTarget;
    private int _promptVariant;
    private string _promptKey;
    private string _promptLabel;
    // 두꺼비집 관련 컴포넌트
    private LightInteraction _lightInteraction;
    private PlayerActionGate _gate;

    // ======== 아이템 던지기(강하게) 관련 변수 ========
    private float gaugeChargeTime = 1.5f; // 게이지가 최대치까지 충전되는 시간
    private float minThrowForce = 2f; // 최소 던지기 힘 -> 0.2초에서 시작
    private float maxThrowForce = 10f; // 최대 던지기 힘 -> 2초에서 최대
    private float _dropKeyPressTime; // 키를 누르기 시작한 시간
    private bool _isChargingThrow = false;
    private float _currentThrowGauge = 0f; // 0 ~ 1 사이의 UI용 게이지 값
    private float _serverThrowChargeStartTime = float.NegativeInfinity; // [서버] 충전 시작 시각. 던지는 힘은 이 시각부터 잰다
    public float CurrentThrowGauge => _currentThrowGauge; // UI에서 접근할 프로퍼티

    private const float THROW_HOLD_THRESHOLD = 0.2f; // 이보다 짧게 누르면 던지지 않고 앞에 툭 내려놓는다

    [Header("Throw Rotation Settings")]
    [SerializeField] private float minThrowTorque = 1f;  // 살짝 던졌을 때의 회전력
    [SerializeField] private float maxThrowTorque = 8f;  // 풀차징으로 던졌을 때의 회전력

    [Header("던지기 소리")]
    [SerializeField] private SoundData dropSound;         // 툭 내려놓을 때 (모두에게 들린다)
    [SerializeField] private SoundData throwSound;        // 힘줘서 던질 때 (모두에게 들린다)
    [SerializeField] private SoundData throwChargeSound;  // 게이지가 차는 동안 반복 (나만 들린다)
    [Tooltip("게이지 0 → 1 동안 충전음 피치. 차오를수록 높아진다")]
    [SerializeField] private Vector2 throwChargePitch = new(0.85f, 1.9f);
    private AudioSource _throwChargeSource;
    
    public RagdollPoser playerRagDollPoser; // 인스펙터에서 연결
    private DraggableObject _activeDragableObject; // 현재 끌고 있는 드래그 오브젝트(예: 이동식 분쇄기)
    [SerializeField] private LayerMask interactLayerMask = ~0;
    private void Awake()
    {
        if (playerCamera == null)
        {
            playerCamera = GetComponentInChildren<Camera>(true);
        }
        _placementPreview = GetComponent<BoxPlacementPreview>();
        _lightInteraction = GetComponent<LightInteraction>();
        _inventory = GetComponent<PlayerInventory>();
        _gate = PlayerActionGate.GetOrAdd(gameObject);

        if (pickupObstacleMask == 0)
        {
            pickupObstacleMask = LayerMask.GetMask("Default", "Ground", "Wall", "Floor", "Stair");
        }
    }

    private void Update()
    {
        if (!IsOwner)
        {
            return;
        }

        UpdateHoveredItem();
        if (_placementPreview != null)
        {
            _placementPreview.UpdatePreview(GetCurrentHeldItem());
        }

        // 던지기 게이지 충전
        if(_isChargingThrow)
        {
            float holdDuration = Time.time - _dropKeyPressTime;

            // 던지기로 바뀌는 순간(0.2초)부터 게이지가 보인다. 그 전에 떼면 툭 내려놓기라 게이지를 띄우지 않는다.
            // 값은 힘 계산과 같은 비율이라 처음 보일 때 이미 조금 차 있고, 1.5초가 지나도 1f를 유지한다
            _currentThrowGauge = holdDuration >= THROW_HOLD_THRESHOLD ? Mathf.Clamp01(holdDuration / gaugeChargeTime) : 0f;
        }

        UpdateThrowChargeSound();
    }

    // 던지기로 인정되는 순간(0.2초)부터 게이지를 따라 피치가 올라가는 충전음을 낸다. 툭 내려놓는 짧은 입력에서는 안 난다
    private void UpdateThrowChargeSound()
    {
        bool charging = _isChargingThrow && Time.time - _dropKeyPressTime >= THROW_HOLD_THRESHOLD;
        if (!charging)
        {
            if (_throwChargeSource != null && _throwChargeSource.isPlaying) _throwChargeSource.Stop();
            return;
        }

        if (_throwChargeSource == null)
        {
            if (throwChargeSound == null || SoundManager.Instance == null) return;
            _throwChargeSource = SoundManager.Instance.CreateLoopSource(throwChargeSound, transform);
            if (_throwChargeSource == null) return;
            _throwChargeSource.volume = throwChargeSound.Volume;
        }

        float pitch = Mathf.Lerp(throwChargePitch.x, throwChargePitch.y, _currentThrowGauge);
        // 다 찼으면 살짝 떨려서 "준비됐다"는 느낌을 준다
        if (_currentThrowGauge >= 1f) pitch += Mathf.Sin(Time.time * 40f) * 0.03f;
        _throwChargeSource.pitch = pitch;

        if (!_throwChargeSource.isPlaying) _throwChargeSource.Play();
    }

    /// <summary>
    /// 구체 내부에서 가장 조준선(카메라 정면)에 가까운 PickupItem을 선별해서 가져옵니다.
    /// </summary>
    private PickupItem GetTargetPickupItem()
    {
        if (playerCamera == null) return null;

        Ray ray = new Ray(playerCamera.transform.position, playerCamera.transform.forward);

        // 1. 먼저 정밀한 Raycast 시도 (작은 아이템이라도 조준점에 정확히 걸리면 최우선)
        if (Physics.Raycast(ray, out RaycastHit rayHit, interactDistance, pickupLayerMask))
        {
            if (rayHit.collider.TryGetComponent<PickupItem>(out PickupItem exactItem) && !IsPickupBlocked(ray.origin, rayHit))
            {
                return exactItem;
            }
        }

        // 2. Raycast 실패 시 SphereCastAll로 주변 영역의 모든 히트 오브젝트 탐색
        RaycastHit[] hits = Physics.SphereCastAll(ray, pickupRadius, interactDistance, pickupLayerMask);

        PickupItem bestItem = null;
        float closestDistance = float.MaxValue;

        foreach (var hit in hits)
        {
            // 플레이어 자신 또는 자식 콜라이더는 제외
            if (hit.collider.transform.IsChildOf(transform)) continue;

            if (hit.collider.TryGetComponent<PickupItem>(out PickupItem item))
            {
                // 가려지지 않은 아이템 중 가장 가까운 거리에 있는 아이템 선택
                if (hit.distance < closestDistance && !IsPickupBlocked(ray.origin, hit))
                {
                    closestDistance = hit.distance;
                    bestItem = item;
                }
            }
        }

        return bestItem;
    }

    // 아이템 표면에 딱 붙은 바닥에 걸려 가려졌다고 판정하지 않도록, 가림 검사는 아이템 표면 조금 앞에서 멈춘다
    private const float PICKUP_BLOCK_CHECK_MARGIN = 0.05f;

    /// <summary>
    /// 카메라와 아이템 사이를 벽/바닥 등(pickupObstacleMask)이 가리고 있는지.
    /// 줍기 레이캐스트는 아이템 레이어만 보므로, 따로 검사하지 않으면 벽 너머나 아래층 아이템도 잡힌다
    /// </summary>
    private bool IsPickupBlocked(Vector3 origin, RaycastHit itemHit)
    {
        // SphereCast가 시작부터 겹친 아이템은 위치 정보가 없지만, 카메라 바로 앞이라 가려질 수 없다
        if (itemHit.distance <= 0f) return false;

        Vector3 toItem = itemHit.point - origin;
        float checkDistance = toItem.magnitude - PICKUP_BLOCK_CHECK_MARGIN;
        if (checkDistance <= 0f) return false;

        return Physics.Raycast(origin, toItem.normalized, checkDistance, pickupObstacleMask, QueryTriggerInteraction.Ignore);
    }

    /// <summary>
    /// 조준선에 걸린 DraggableObject를 가져옵니다. 하이라이트와 끌기 시작이 같은 판정을 쓰도록 둘 다 이 함수를 사용합니다. <br/>
    /// 콜라이더가 모델(자식)에 붙어 있는 경우가 많으므로 맞은 콜라이더의 부모까지 찾습니다.
    /// </summary>
    private DraggableObject GetTargetDraggable()
    {
        if (playerCamera == null) return null;

        if (Physics.Raycast(playerCamera.transform.position, playerCamera.transform.forward, out RaycastHit hit, interactDistance, interactLayerMask))
        {
            return hit.collider.GetComponentInParent<DraggableObject>();
        }
        return null;
    }

    /// <summary>
    /// 줍기 키 입력 처리
    /// </summary>
    public void OnPickupInput(InputAction.CallbackContext context)
    {
        if (!IsOwner || !context.started) return;

        // 이미 파쇄기를 끌고 있다면 줍기 스킵 (중복 방지)
        if (_activeDragableObject != null)
        {
            if (!_gate.CanDo(PlayerAction.ReleaseDrag)) return;

            RequestStopDragServerRpc(new NetworkObjectReference(_activeDragableObject.NetworkObject));
            GetComponent<PlayerMovement>()?.SetDraggleObject(null);
            // 래그돌 손 뻗기 자세 해제
            if (playerRagDollPoser != null)
            {
                playerRagDollPoser.SetCarryRequested(false);
                playerRagDollPoser.SetCarryTarget(null);
            }

            _activeDragableObject = null;
            return;
        }

        // 이미 아이템을 들고 있다면 줍기 스킵 (중복 방지)
        if (IsHoldingItem())
        {
            if (!_gate.CanDo(PlayerAction.PlaceBox)) return;

            if (_placementPreview != null && _placementPreview.IsPreviewValid)
            {
                // 프리뷰가 올바른 상태이므로 서버에 박스 배치 요청
                TryPlaceBoxServerRpc(_placementPreview.CurrentPreviewPosition);
                _placementPreview.ClearPreview();
            }
            else
            {
                Debug.Log("상자를 들고 있지만 배치가 불가능한 영역입니다.");
            }
            return; // 배치를 시도했으므로 아래 줍기 로직은 타지 않음
        }

        // 3. 손에 아무것도 없고, 바라보는 곳에 파쇄기가 있는지 확인
        DraggableObject draggable = GetTargetDraggable();
        if (draggable != null)
        {
            if (CanStartDrag(draggable))
            {
                _activeDragableObject = draggable;
                RequestStartDragServerRpc(new NetworkObjectReference(_activeDragableObject.NetworkObject));
                GetComponent<PlayerMovement>()?.SetDraggleObject(_activeDragableObject);
                return;
            }
        }

        // 4. 손에 아무것도 없고, 바라보는 곳에 줍기 가능한 아이템이 있는지 확인
        if (!_gate.CanDo(PlayerAction.Pickup)) return;

        // 통합 탐색 함수 사용
        PickupItem targetItem = GetTargetPickupItem();
        if (targetItem != null)
        {
            TryPickupServerRpc(new NetworkObjectReference(targetItem.NetworkObject));
        }
    }

    /// <summary>
    /// 버리기 키 입력 처리 (박스 배치 포함)
    /// </summary>
    public void OnDropInput(InputAction.CallbackContext context)
    {
        if (!IsOwner) return;

        // 손에 든 아이템이 있어야만 버릴 수 있음
        if (context.started)
        {
            if (!_gate.CanDo(PlayerAction.DropItem)) return;

            _dropKeyPressTime = Time.time;
            _isChargingThrow = true;
            _currentThrowGauge = 0f;
            StartThrowChargeServerRpc();
        }
        else if (context.canceled)
        {
            // 예외 방어: 충전 중이 아니었다면 리턴
            if (!_isChargingThrow) return;

            _isChargingThrow = false;

            // 클라이언트는 "뗐다"만 알린다. 누른 시간과 던지는 방향은 서버가 정한다
            RequestDropOrThrowServerRpc();

            // 로컬 수치 초기화
            _currentThrowGauge = 0f;
            if (_placementPreview != null) _placementPreview.ClearPreview();
        }
    }

    /// <summary>
    /// 파쇄기 등 DraggableObject 끌기 시작 요청. 호출자가 소유한 PlayerInteraction 위에서 RPC를 받아
    /// 서버에서 직접 대상 오브젝트의 로직을 호출한다 (대상 오브젝트는 호출자 소유가 아니므로 이렇게 우회해야 함).
    /// </summary>
    [ServerRpc]
    private void RequestStartDragServerRpc(NetworkObjectReference draggableReference)
    {
        // 상호 배타 규칙 서버 재검증(치트 방어). 이미 무언가 끌고 있으면 게이트(RESTRAINED ⊃ DraggingObject)가 중복 시작도 막는다
        if (!_gate.CanDo(PlayerAction.DragObject)) return;

        if (!draggableReference.TryGet(out NetworkObject draggableNetworkObject)
            || !draggableNetworkObject.TryGetComponent<DraggableObject>(out var draggable))
        {
            return;
        }

        // 클라이언트가 지정한 대상을 믿지 않고, 서버에서 실제 상호작용 거리 안인지 확인
        if (!IsWithinDragRange(draggable)) return;

        draggable.StartDrag(NetworkObjectId);

        // 이동 둔화는 서버의 PlayerMovement가 판정하므로, 서버 쪽 인스턴스에도 끌고 있는 대상을 알려줘야 한다
        if (draggable.GrabberPlayerId == NetworkObjectId)
        {
            GetComponent<PlayerMovement>()?.SetDraggleObject(draggable);
        }
    }

    // 조준 지연으로 정상 요청이 튕기지 않도록 interactDistance에 여유를 둔다 (PickupItem.CanBePickedUpBy와 같은 방식)
    private const float DRAG_RANGE_TOLERANCE = 1f;

    private bool IsWithinDragRange(DraggableObject draggable)
    {
        Vector3 origin = playerCamera != null ? playerCamera.transform.position : transform.position;
        float maxDistance = interactDistance + DRAG_RANGE_TOLERANCE;

        // 큰 오브젝트라 중심점이 아니라 콜라이더 경계 기준으로 거리를 잰다 (bounds는 비볼록 MeshCollider에도 안전)
        Collider[] colliders = draggable.GetComponentsInChildren<Collider>();
        if (colliders.Length == 0)
        {
            return Vector3.Distance(origin, draggable.transform.position) <= maxDistance;
        }

        foreach (Collider col in colliders)
        {
            if (Vector3.Distance(origin, col.bounds.ClosestPoint(origin)) <= maxDistance)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// DraggableObject 끌기 해제 요청. 위와 동일한 이유로 호출자 소유 오브젝트를 거쳐 처리한다.
    /// </summary>
    [ServerRpc]
    private void RequestStopDragServerRpc(NetworkObjectReference draggableReference)
    {
        if (draggableReference.TryGet(out NetworkObject draggableNetworkObject)
            && draggableNetworkObject.TryGetComponent<DraggableObject>(out var draggable)
            && draggable.GrabberPlayerId == NetworkObjectId) // 내가 끌고 있는 것만 해제 가능 (NetworkObjectId는 요청을 보낸 플레이어의 ID)
        {
            draggable.StopDrag();
            GetComponent<PlayerMovement>()?.SetDraggleObject(null);
        }
    }

    /// <summary>
    /// 끌던 오브젝트의 잡기가 풀렸을 때 DraggableObject가 호출한다 (직접 놓은 경우 + 서버가 거리 초과로 강제 해제한 경우). <br/>
    /// 강제 해제 시 소유자에게 _activeDragableObject가 남아 있으면 다음 줍기 입력이 '놓기'로 헛돌기 때문에 여기서 정리한다
    /// </summary>
    public void OnDragReleased(DraggableObject draggable)
    {
        if (_activeDragableObject == draggable)
        {
            _activeDragableObject = null;
        }

        GetComponent<PlayerMovement>()?.SetDraggleObject(null);
    }

    /// <summary>
    /// 끌던 오브젝트를 놓습니다. 피격으로 쓰러지거나 남에게 붙잡히는 등 플레이어 의사와 무관하게 놓칠 때 호출합니다. [서버 전용] <br/>
    /// 게이트는 끌기 '시작'만 막으므로, 끄는 도중 이런 상태가 되면 이렇게 직접 놓아야 한다 (잡기/들고 있는 아이템과 같은 방식)
    /// </summary>
    public void ServerReleaseDrag()
    {
        if (!IsServer) return;

        PlayerMovement movement = GetComponent<PlayerMovement>();
        DraggableObject draggable = movement != null ? movement.CurrentDraggingObject : null;
        if (draggable != null)
        {
            // 놓으면 DraggableObject가 OnDragReleased로 서버/소유자 양쪽 끌기 상태를 정리한다
            draggable.StopDrag();
        }
    }

    [ServerRpc]
    public void TryPickupServerRpc(NetworkObjectReference pickupReference)
    {
        // 상호 배타 규칙 서버 재검증(치트 방어). 마피아 쓰레기 생성은 별도 규칙이라 여기를 타지 않는다
        if (!_gate.CanDo(PlayerAction.Pickup)) return;

        PickupLogicalServer(pickupReference);
    }

    /// <summary>
    /// 실제 서버에서 아이템 줍기를 처리하는 로직 (RPC가 아니므로 서버 내부에서 자유롭게 호출 가능)
    /// </summary>
    public void PickupLogicalServer(NetworkObjectReference pickupReference, bool forcePickup = false)
    {
        if (!IsServer) return; // 서버 측 방어 코드

        if (IsHoldingItem() || !pickupReference.TryGet(out NetworkObject pickupNetworkObject))
        {
            return;
        }

        if (!pickupNetworkObject.TryGetComponent<PickupItem>(out PickupItem pickupItem))
        {
            return;
        }

        if (!forcePickup && !pickupItem.CanBePickedUpBy(this))
        {
            return;
        }

        if (_inventory.TryAddItem(pickupItem))
        {
            // 인벤토리에 들어갔으므로 줍기 실행 (부모 설정 및 RPC 전달)
            pickupItem.Pickup(this);
            _inventory.RefreshInventoryVisuals();
        }
        else
        {
            Debug.Log("인벤토리가 가득 찼습니다!");
        }
    }

    private void UpdateHoveredItem()
    {
        // 줍기/끌기와 같은 탐색 함수를 사용해서 "하이라이트가 뜨면 반드시 주울(끌) 수 있음"을 보장
        PickupItem newHoveredItem = GetTargetPickupItem();
        DraggableObject newHoveredDraggable = GetTargetDraggable();

        // 바라보는 대상이 바뀌면 이전 대상의 하이라이트를 끈다
        if (newHoveredDraggable != hoveredDraggable)
        {
            if (hoveredDraggable != null)
            {
                hoveredDraggable.SetHighlighted(false);
            }
            hoveredDraggable = newHoveredDraggable;
        }

        if (newHoveredItem != hoveredItem)
        {
            SetHoveredHighlight(false);
            hoveredItem = newHoveredItem;
        }

        // 대상이 그대로여도 상호작용 가능 여부는 바뀐다 (끌던 걸 놓은 직후, 손에 든 아이템을 내려놓은 뒤 등).
        // 대상이 바뀔 때만 판정하면 그 상태로 계속 바라보는 동안 하이라이트가 안 켜지므로 매 프레임 다시 판정한다
        if (hoveredDraggable != null)
        {
            hoveredDraggable.SetHighlighted(CanStartDrag(hoveredDraggable));
        }
        SetHoveredHighlight(CanPickupHoveredItem());

        UpdateHoveredButton();
        UpdatePrompt();
    }

    /// <summary>
    /// 투표 버튼이나 차단기를 바라보면 아웃라인을 켠다. 투표 버튼은 쿨다운 중에도 켜서 남은 시간을 보여 준다
    /// </summary>
    private void UpdateHoveredButton()
    {
        Component target = null;

        if (playerCamera != null
            && Physics.Raycast(playerCamera.transform.position, playerCamera.transform.forward, out RaycastHit hit, interactDistance, interactLayerMask))
        {
            VoteManager vote = VoteManager.Instance;
            VoteButton voteButton = hit.collider.GetComponentInParent<VoteButton>();
            LightSwitch lightSwitch = hit.collider.GetComponentInParent<LightSwitch>();

            if (voteButton != null && vote != null && vote.IsSpawned && vote.Phase.Value == VotePhase.None && _gate.CanDo(PlayerAction.CallVote))
            {
                target = voteButton;
            }
            else if (lightSwitch != null && _gate.CanDo(PlayerAction.ToggleLight) && CanUseLightSwitch(lightSwitch))
            {
                target = lightSwitch;
            }
        }

        if (target == hoveredButton) return;

        if (hoveredButton != null) PickupHighlight.SetHighlightedInChildren(hoveredButton, false);
        hoveredButton = target;
        if (hoveredButton != null) PickupHighlight.SetHighlightedInChildren(hoveredButton, true);
    }

    /// <summary>
    /// 불이 켜져 있으면 마피아만 내릴 수 있고, 꺼져 있으면 누구나 길게 눌러 복구한다 (LightInteraction 서버 규칙과 같다)
    /// </summary>
    private bool CanUseLightSwitch(LightSwitch lightSwitch)
    {
        if (!lightSwitch.IsLightOn.Value) return true;

        return TryGetComponent(out RoleManager role) && role.CurrentRole == PlayerRole.Mafia;
    }

    // 아이템 데이터가 없는 프리팹은 오브젝트 이름이라도 보여 주되, 생성할 때 붙는 "(Clone)"은 뗀다
    private static string NameOf(PickupItem item)
    {
        return item.ItemData != null ? item.ItemData.LocalizedName : item.name.Replace("(Clone)", "").Trim();
    }

    /// <summary>
    /// 아웃라인이 켜진 물체에 무엇을 할 수 있는지 키와 이름을 띄운다. 아웃라인과 같은 조건을 쓴다
    /// </summary>
    private void UpdatePrompt()
    {
        InteractPromptUI prompt = InteractPromptUI.Instance;
        if (prompt == null) return;

        Object target = null;
        int variant = 0;

        // 일시정지 메뉴(키 설정 포함)가 열려 있으면 감춘다. 닫으면 대상을 새로 잡으므로 바뀐 키가 바로 보인다
        PauseMenuController menu = PauseMenuController.Instance;
        bool paused = menu != null && menu.IsOpen;

        PickupItem heldItem = GetCurrentHeldItem();

        if (paused) target = null;
        // 상자를 들고 초록 가이드가 뜨는 곳을 보면 배치 안내 (배치 키 처리와 같은 조건)
        else if (heldItem != null && _placementPreview != null && _placementPreview.IsPreviewValid && _gate.CanDo(PlayerAction.PlaceBox))
        {
            target = heldItem;
            variant = PLACE_VARIANT;
        }
        else if (hoveredDraggable != null && CanStartDrag(hoveredDraggable)) target = hoveredDraggable;
        else if (hoveredItem != null && CanPickupHoveredItem()) target = hoveredItem;
        else if (hoveredButton != null) target = hoveredButton;

        if (target == null)
        {
            _promptTarget = null;
            prompt.Hide();
            return;
        }

        if (target is VoteButton && VoteManager.Instance != null) variant = Mathf.CeilToInt(VoteManager.Instance.CooldownRemaining);
        if (target is LightSwitch litSwitch) variant = litSwitch.IsLightOn.Value ? 1 : 2;   // 보고 있는 중에 불이 꺼지거나 켜지면 문구를 바꾼다

        if (target != _promptTarget || variant != _promptVariant)
        {
            _promptTarget = target;
            _promptVariant = variant;
            switch (target)
            {
                case DraggableObject draggable:
                    _promptKey = InteractPromptUI.KeyOf(PICKUP_ACTION);
                    _promptLabel = InteractPromptUI.Translate("interact_drag", draggable.LocalizedName);
                    break;
                case PickupItem item when variant == PLACE_VARIANT:
                    _promptKey = InteractPromptUI.KeyOf(PICKUP_ACTION);
                    _promptLabel = InteractPromptUI.Translate("interact_place", NameOf(item));
                    break;
                case PickupItem item:
                    _promptKey = InteractPromptUI.KeyOf(PICKUP_ACTION);
                    _promptLabel = InteractPromptUI.Translate("interact_pickup", NameOf(item));
                    break;
                case LightSwitch lightSwitch:
                    _promptKey = InteractPromptUI.KeyOf(BUTTON_ACTION);
                    _promptLabel = InteractPromptUI.Translate(lightSwitch.IsLightOn.Value ? "interact_use" : "interact_restore", lightSwitch.LocalizedName);
                    break;
                default:
                    _promptKey = InteractPromptUI.KeyOf(BUTTON_ACTION);
                    _promptLabel = VoteButton.PromptLabel(variant);
                    break;
            }
        }

        prompt.Show(_promptKey, _promptLabel);
    }

    /// <summary>
    /// 지금 이 DraggableObject를 끌기 시작할 수 있는지. 하이라이트와 끌기 시작이 같은 조건을 쓴다
    /// </summary>
    private bool CanStartDrag(DraggableObject draggable)
    {
        // 이미 끌고 있거나 끌기 요청을 보낸 상태면 하이라이트하지 않는다 (요청 후 동기화되기 전까지 IsBeingDragged가 false라서 따로 확인)
        // 다른 사람이 끌고 있지 않고, 서버 검증과 같은 게이트를 통과할 때만 시작 (거절돼서 서버와 상태가 어긋나는 것 방지)
        return _activeDragableObject == null
            && !draggable.IsBeingDragged
            && _gate.CanDo(PlayerAction.DragObject);
    }

    /// <summary>
    /// 지금 바라보는 아이템을 주울 수 있는지
    /// </summary>
    private bool CanPickupHoveredItem()
    {
        if (hoveredItem == null) return false;

        // 1. 줍기 규칙(붙잡힘, 손 점유 등)은 게이트가 판정
        if (!_gate.CanDo(PlayerAction.Pickup)) return false;

        // 2. 페인트 총(4번)을 들고 있을 때는 백팩(1~3번)에 자리가 있어야 함
        if (_inventory != null
            && _inventory.CurrentSlot == PlayerActionGate.PAINT_GUN_SLOT
            && _inventory.IsBackpackFull())
        {
            return false;
        }

        return true;
    }

    public override void OnNetworkDespawn()
    {
        SetHoveredHighlight(false);
        hoveredItem = null;

        if (hoveredButton != null) PickupHighlight.SetHighlightedInChildren(hoveredButton, false);
        hoveredButton = null;
        if (IsOwner && InteractPromptUI.Instance != null) InteractPromptUI.Instance.Hide();

        // 혹시 인벤토리에 들고 있는 아이템이 있었으면 다 한번에 내려놓게 하기
    }

    private void SetHoveredHighlight(bool highlighted)
    {
        if (hoveredItem == null)
        {
            return;
        }

        PickupHighlight pickupHighlight = hoveredItem.GetComponent<PickupHighlight>();
        if (pickupHighlight != null)
        {
            pickupHighlight.SetHighlighted(highlighted);
        }
    }

    /// <summary>
    /// 들고 있는 박스를 정렬 영역이나 기존 박스 위에 정렬 배치 시도
    /// </summary>
    [ServerRpc]
    private void TryPlaceBoxServerRpc(Vector3 requestedPosition)
    {
        if (!_gate.CanDo(PlayerAction.PlaceBox)) return;

        PickupItem currentHeldItem = GetCurrentHeldItem();
        if (!IsHoldingItem() || currentHeldItem == null) return;

        // 손이 닿지 않는 먼 거리에 배치 요청하는 것 방지
        // 클라이언트 프리뷰와 같은 기준(카메라 → 배치 위치 3D 거리)으로 검사해서 다른 층 구역에 놓는 것도 막는다 (네트워크 지연만큼 여유)
        Vector3 origin = playerCamera != null ? playerCamera.transform.position : transform.position;
        if (Vector3.Distance(origin, requestedPosition) > _placementPreview.MaxPlacementDistance + 0.5f)
        {
            DropHeldItemStandard();
            return;
        }

        // 클라이언트가 보낸 좌표 발밑에 실제 바닥 영역(PlacementZone)이 여전히 있는지 확인
        PlacementZone targetZone = null;
        int zoneLayerMask = LayerMask.GetMask("PlacementZone");

        // 요청된 위치 혹은 그 약간 아래에서 레이를 쏘아 바닥 구역을 역추적함.
        if (Physics.Raycast(requestedPosition + Vector3.up * 0.1f, Vector3.down, out RaycastHit groundHit, 50f, zoneLayerMask))
        {
            targetZone = groundHit.collider.GetComponent<PlacementZone>();
        }

        // 바닥이 존재하고, 가로세로 영역 내에 있으며, '서버 시점'에서도 그 자리가 비어있는지 확인.
        if (targetZone != null && PlacementValidator.IsValidPlacement(requestedPosition, targetZone, _placementPreview.gBoxSize, currentHeldItem.gameObject))
        {
            if (currentHeldItem.TryGetComponent<PlaceableBox>(out var placeableBox))
            {
                currentHeldItem.Drop();
                placeableBox.PlaceAt(requestedPosition, Quaternion.identity);
                GetComponent<PlayerData>().ItemsOrganized.Value++;
                return;
            }
        }
        // 실패하면 그냥 들고 있던 아이템을 그대로 떨어뜨리기
        DropHeldItemStandard();
    }
    
    /// <summary>
    /// 투표룸·게임 종료 무대처럼 다른 곳으로 옮기기 전에 손을 비운다. 잡던 사람과 끌던 물건은 놓고,
    /// 들고 있던 아이템은 가방에 넣은 채 빈손 슬롯(0)으로 바꾼다. 원래 슬롯을 돌려준다. [서버 전용]
    /// </summary>
    public int ServerEmptyHands()
    {
        if (!IsServer) return 0;

        GetComponent<PlayerGrab>()?.ServerReleaseGrab();
        ServerReleaseDrag();
        if (_inventory == null) return 0;

        int slot = _inventory.CurrentSlot;
        _inventory.ServerForceSlot(0);
        return slot;
    }

    [ServerRpc]
    private void DropHeldItemServerRpc()
    {
        DropHeldItemStandard();
    }

    /// <summary>
    /// 들고 있던 아이템을 그 자리에 떨어뜨립니다. 피격처럼 플레이어 의사와 무관하게 놓칠 때 호출합니다. [서버 전용]
    /// </summary>
    public void ServerDropHeldItem()
    {
        DropHeldItemStandard();
    }

    // 중복 코드를 줄이기 위한 내부 실제 드롭 처리 함수
    private void DropHeldItemStandard()
    {
        if (!IsServer) return;

        // 인벤토리의 현재 슬롯에 구현된 아이템의 Drop을 호출합니다.
        if (_inventory != null)
        {
            _inventory.DropCurrentItemDirectServer();
        }
    }

    public bool IsHoldingItem()
    {
        if (_inventory == null) return false;

        // 인벤토리에서 현재 들고 있는 아이템이 null이 아니면 true
        return _inventory.GetCurrentEquippedItem() != null;
    }
    /// <summary>
    /// 네트워크 변수로부터 현재 들고 있는 PickupItem 컴포넌트를 가져온다.
    /// </summary>
    public PickupItem GetCurrentHeldItem()
    {
        if (_inventory == null) return null;
        return _inventory.GetCurrentEquippedItem();
    }

    // 조준 가능한 아이템 (폴라로이드 카메라 등)
    public void OnAim(InputAction.CallbackContext context)
    {
        if (!IsOwner) return;
        if (context.performed && !_gate.CanDo(PlayerAction.UseTool)) return;
        if (!IsHoldingItem()) return;
        var heldItem = GetCurrentHeldItem();
        if (heldItem == null || !heldItem.TryGetComponent<IZoomTool>(out var zoomToolTool)) return;
        if (context.performed) zoomToolTool.Aim(true);
        else if (context.canceled) zoomToolTool.Aim(false);
    }

    public void OnCapture(InputAction.CallbackContext context)
    {
        if (!IsOwner) return;
        if (!context.performed) return;
        if (!_gate.CanDo(PlayerAction.UseTool)) return;
        var heldItem = GetCurrentHeldItem();
        if (heldItem == null || !heldItem.TryGetComponent<ICaptureTool>(out var captureTool)) return;

        captureTool.Capture();
    }
    // 두꺼비집 동작 콜백
    public void OnToggleLight(InputAction.CallbackContext context)
    {
        if (!IsOwner) return;
        if (_lightInteraction == null) return;

        if (context.started)
        {
            if (!_gate.CanDo(PlayerAction.ToggleLight)) return;
            if (playerCamera == null) return;

            // 앞에 스위치가 있는지 레이캐스트 검사만 수행
            if (Physics.Raycast(playerCamera.transform.position, playerCamera.transform.forward, out RaycastHit hit, interactDistance, interactLayerMask))
            {
                if (hit.collider.TryGetComponent<LightSwitch>(out LightSwitch lightSwitch))
                {
                    _lightInteraction.StartLightInteraction(lightSwitch);
                }
                else if (hit.collider.GetComponentInParent<VoteButton>() != null && VoteManager.Instance != null && _gate.CanDo(PlayerAction.CallVote))
                {
                    VoteManager.Instance.CallMeeting();
                }
            }
        }
        else if (context.canceled)
        {
            // 손 떼면 취소하라고 신호만 보냄
            _lightInteraction.CancelLightInteraction();
        }
    }

    #region [아이템 던지기 관련 로직]

    [ServerRpc]
    private void StartThrowChargeServerRpc()
    {
        if (!_gate.CanDo(PlayerAction.DropItem)) return;
        if (GetCurrentHeldItem() == null) return;

        _serverThrowChargeStartTime = Time.time;
    }

    [ServerRpc]
    private void RequestDropOrThrowServerRpc()
    {
        if (!IsServer) return;

        // 누른 시간은 서버가 받은 누름·뗌 요청 사이의 시간으로 잰다. 누름 요청이 거부됐으면 힘 없이 내려놓는다
        float holdDuration = float.IsNegativeInfinity(_serverThrowChargeStartTime) ? 0f : Time.time - _serverThrowChargeStartTime;
        _serverThrowChargeStartTime = float.NegativeInfinity;

        if (!_gate.CanDo(PlayerAction.DropItem)) return;

        PickupItem currentHeldItem = GetCurrentHeldItem();
        if (currentHeldItem == null) return;

        // 던지는 방향은 서버에 동기화된 이 플레이어의 시점
        Vector3 direction = playerCamera != null ? playerCamera.transform.forward : transform.forward;

        // 1. 공통 처리: 먼저 아이템 인벤토리 관계 해제 및 가시성 회복
        currentHeldItem.SetVisibility(true);
        if (_inventory != null)
        {
            _inventory.ClearItemFromSlots(currentHeldItem);
        }

        // 던지는 플레이어 id구함
        ulong throwerNetId = this.NetworkObjectId;
        // 2. 누른 시간에 따라 분기 처리
        Vector3 soundPosition = currentHeldItem.transform.position;
        if (holdDuration < THROW_HOLD_THRESHOLD)
        {
            // 0.2초 미만: 그냥 앞에 툭 떨어뜨리기 (힘 0)
            currentHeldItem.ThrowFromServer(direction, 0f, Vector3.zero, throwerNetId);
            PlayReleaseSoundClientRpc(soundPosition, false, 0f);
        }
        else
        {
            // 0.2초 이상: 서버에서 Force를 연산하여 물리 발사
            float clampedProgress = Mathf.Clamp01(holdDuration / gaugeChargeTime);
            float finalForce = Mathf.Lerp(minThrowForce, maxThrowForce, clampedProgress);
            float finalTorqueMagnitude = Mathf.Lerp(minThrowTorque, maxThrowTorque, clampedProgress);
            Vector3 randomTorque = Random.insideUnitSphere.normalized * finalTorqueMagnitude;
            currentHeldItem.ThrowFromServer(direction, finalForce, randomTorque, throwerNetId);
            PlayReleaseSoundClientRpc(soundPosition, true, clampedProgress);
        }
    }

    // 내려놓기·던지기 소리. 세게 던질수록 크게 들린다
    [ClientRpc]
    private void PlayReleaseSoundClientRpc(Vector3 position, bool thrown, float strength)
    {
        SoundData sound = thrown ? throwSound : dropSound;
        if (sound == null || SoundManager.Instance == null) return;

        SoundManager.Instance.PlaySFXAt(sound, position, thrown ? Mathf.Lerp(0.6f, 1f, strength) : 1f);
    }

    #endregion

    #region 아이템 사용 통합 처리 (IUsableItem 인터페이스) -> 흡입기, 테이저건, 청소 도구
    /// <summary>
    /// 마우스 좌클릭 통합 입력 콜백 (Input Action: UseItem 에 바인딩)
    /// </summary>
    public void OnUseItem(InputAction.CallbackContext context)
    {
        if (!IsOwner) return;

        // 1. 행동 가능 여부(게이트) 검사. 떼는 입력은 항상 통과시켜야 사용 상태가 켜진 채로 남지 않는다
        if (context.performed && !_gate.CanDo(PlayerAction.UseTool)) return;

        // 2. PlayerInventory를 통해 현재 장착한 아이템 가져오기
        PickupItem currentItem = _inventory.GetCurrentEquippedItem();
        if (currentItem == null) return;

        // 3. IUsableItem 인터페이스가 존재하면 OnUse 호출 (테이저건, 흡입기, 청소 도구)
        if (currentItem.TryGetComponent<IUsableItem>(out var usableItem))
        {
            usableItem.OnUse(context, playerCamera);
        }
    }
    #endregion
}