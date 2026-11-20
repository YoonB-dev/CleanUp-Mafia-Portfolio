using UnityEngine;

/// <summary>
/// 대걸레를 든 자세. 평소엔 다른 들기 아이템처럼 손 앵커 기준 고정 각도로 들고 다니고(시선을 따라 돈다),
/// 닦는 동안엔 걸레 머리를 닦이는 자리로 가져가 좌우로 문지른다. 두 자세 사이는 부드럽게 넘어간다.
/// 아이템 루트는 손 앵커(CarryAnchor)에 붙어 있으므로 루트는 건드리지 않고 모델 자식만 움직이며, 내려놓으면 원래 자리로 되돌린다.
/// 손 위치와 청소 지점(PaintCleaner의 ClientRpc)은 모든 클라이언트가 이미 알고 있으므로 따로 동기화하지 않는다.
/// 모델은 피벗이 걸레 머리 바닥, +Y가 자루 끝 방향이어야 한다 (SM_Mop_01).
/// </summary>
[DefaultExecutionOrder(1000)] // RagdollPoser.LateUpdate가 손 앵커를 옮긴 뒤에 계산해야 한 프레임 늦지 않는다
[RequireComponent(typeof(PickupItem))]
public class MopHoldPose : MonoBehaviour
{
    [Header("References")]
    [Tooltip("움직일 걸레 모델 (피벗 = 걸레 머리 바닥, +Y = 자루 끝)")]
    [SerializeField] private Transform model;
    [SerializeField] private PaintCleaner cleaner;

    [Header("들었을 때 크기")]
    [Tooltip("들고 있는 동안 자루 길이 배율. 바닥에 놓인 모습은 그대로 두고 손에 들 때만 짧게 만든다")]
    [SerializeField, Range(0.2f, 1f)] private float heldLengthScale = 0.45f;
    [Tooltip("들고 있는 동안 자루 굵기·걸레 머리 폭 배율. 길이만 줄이면 머리가 납작해지므로 덜 줄여서 비율을 맞춘다")]
    [SerializeField, Range(0.2f, 1f)] private float heldThicknessScale = 0.8f;

    [Header("쥐는 곳")]
    [Tooltip("자루가 지나가는 지점. 두 손 가운데에서 손 앵커 방향 기준으로 (X:오른쪽, Y:위, Z:앞, m). 0이면 자루가 두 손 사이를 지나 손에 쥔 것처럼 보인다")]
    [SerializeField] private Vector3 gripOffset = Vector3.zero;

    [Header("들고 다닐 때 (닦지 않을 때)")]
    [Tooltip("자루를 앞으로 숙이는 각도 (시선에서 아래로, 도). 0이면 정면으로 뻗는다. 크게 숙이면 화면 아래로 빠져 들고 있는지 잘 안 보인다.\n다른 들기 아이템처럼 손 앵커에 고정돼 시선을 따라 돌기 때문에, 위를 보면 대걸레도 같이 들린다")]
    [SerializeField, Range(-30f, 80f)] private float carryPitch = 10f;
    [Tooltip("자루를 옆으로 돌리는 각도 (도, +면 오른쪽). 1인칭 화면 가운데를 가리지 않게 살짝 비킨다")]
    [SerializeField, Range(-60f, 60f)] private float carryYaw = 15f;
    [Tooltip("들고 다닐 때 손 뒤로 남는 자루 길이 (m)")]
    [SerializeField, Min(0f)] private float carryHandleBehindGrip = 0.4f;

    [Header("닦을 때")]
    [Tooltip("손 뒤로 남는 자루 길이 범위 (m). 닦는 자리가 가까우면 최대까지, 멀면 최소까지 자루가 손 안에서 미끄러진다")]
    [SerializeField] private Vector2 handleBehindGrip = new(0.15f, 0.4f);
    [Tooltip("걸레 머리가 닦는 자리를 따라가는 빠르기. 크면 바로 붙고, 작으면 묵직하게 따라온다")]
    [SerializeField, Min(0.1f)] private float followSpeed = 12f;
    [Tooltip("초당 좌우 왕복 횟수")]
    [SerializeField, Min(0f)] private float scrubFrequency = 2.2f;
    [Tooltip("좌우로 문지르는 폭 (m, 한쪽)")]
    [SerializeField, Min(0f)] private float scrubWidth = 0.28f;
    [Tooltip("앞뒤로 밀고 당기는 폭 (m, 한쪽). 좌우의 두 배 빠르기로 움직여 8자를 그린다")]
    [SerializeField, Min(0f)] private float scrubPush = 0.12f;
    [Tooltip("들고 다니는 자세 ↔ 닦는 자세 전환 빠르기 (초당)")]
    [SerializeField, Min(0.1f)] private float poseBlendSpeed = 6f;

    private const float DEFAULT_HANDLE_LENGTH = 1.89f; // SM_Mop_01 메시 길이 (모델 로컬)
    private const float HEAD_LIFT = 0.04f;            // 걸레 머리가 면에 파묻히지 않게 살짝 띄운다
    private const float MAX_CARRY_PITCH = 80f;        // 시선 + 숙임이 이보다 크면 자루가 수직을 넘어 몸 뒤로 넘어가므로 막는다

    private PickupItem _pickup;
    private RagdollPoser _holderPoser; // 들고 있는 사람. 손 위치를 직접 읽어 자루를 손에 붙인다
    private Vector3 _restLocalPosition;
    private Quaternion _restLocalRotation;
    private Vector3 _restLocalScale;
    private float _meshHandleLength = DEFAULT_HANDLE_LENGTH;

    private bool _wasHeld;
    private float _cleanWeight;    // 0 = 들고 다니는 자세, 1 = 닦는 자세
    private bool _hasCleanTarget;
    private Vector3 _cleanTarget;
    private Vector3 _cleanNormal = Vector3.up;
    private float _scrubPhase;

    private void Awake()
    {
        _pickup = GetComponent<PickupItem>();
        if (cleaner == null) cleaner = GetComponent<PaintCleaner>();
        if (model == null) return;

        _restLocalPosition = model.localPosition;
        _restLocalRotation = model.localRotation;
        _restLocalScale = model.localScale;

        // 메시는 Read/Write가 꺼져 있어도 bounds는 읽을 수 있다
        var meshFilter = model.GetComponentInChildren<MeshFilter>();
        if (meshFilter != null && meshFilter.sharedMesh != null && meshFilter.sharedMesh.bounds.max.y > 0.01f)
        {
            _meshHandleLength = meshFilter.sharedMesh.bounds.max.y;
        }
    }

    private void LateUpdate()
    {
        if (model == null) return;

        bool held = _pickup.IsHeld && transform.parent != null;
        if (!held)
        {
            if (_wasHeld) RestoreModel();
            _wasHeld = false;
            _holderPoser = null;
            return;
        }
        if (!_wasHeld)
        {
            _wasHeld = true;
            _hasCleanTarget = false;
            _cleanWeight = 0f;
            // 손 앵커(CarryAnchor)는 RagdollPoser 아래에 붙어 있다
            _holderPoser = transform.parent.GetComponentInParent<RagdollPoser>();

            // 모델 +Y가 자루 방향이라 로컬 Y만 길이로 줄인다. 아래 자루 길이 계산(lossyScale)에 바로 반영된다
            model.localScale = Vector3.Scale(_restLocalScale, new Vector3(heldThicknessScale, heldLengthScale, heldThicknessScale));
        }

        float deltaTime = Time.deltaTime;
        float follow = 1f - Mathf.Exp(-followSpeed * deltaTime);
        float handleLength = _meshHandleLength * model.lossyScale.y;

        // 수평 정면. 닦을 때 문지르는 방향을 못 정할 때만 쓴다
        Vector3 forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
        if (forward.sqrMagnitude < 1e-4f) forward = Vector3.ProjectOnPlane(transform.up, Vector3.up);
        if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
        forward.Normalize();

        // 앵커는 손보다 앞·위에 떠 있고 아이템마다 오프셋이 달라서, 자루는 실제 두 손 가운데를 지나게 한다
        Vector3 hands = transform.position;
        if (_holderPoser != null && _holderPoser.LeftHand != null && _holderPoser.RightHand != null)
        {
            hands = (_holderPoser.LeftHand.position + _holderPoser.RightHand.position) * 0.5f;
        }
        Vector3 grip = hands + transform.rotation * gripOffset;

        // 다른 들기 아이템처럼 손 앵커(시선 상하·좌우를 담고 있음) 기준 고정 각도로 든다
        Quaternion carryFrame = transform.rotation * Quaternion.Euler(carryPitch, carryYaw, 0f);
        Vector3 head = CarryHead(grip, carryFrame, handleLength);

        bool cleaning = cleaner != null && cleaner.IsCleaningContact;
        _cleanWeight = Mathf.MoveTowards(_cleanWeight, cleaning ? 1f : 0f, poseBlendSpeed * deltaTime);

        if (_cleanWeight > 0.001f)
        {
            // 닦기 시작하면 들고 있던 자리에서 출발해 닦는 자리로 따라간다. 놓으면 마지막 자리에서 들고 다니는 자세로 돌아온다
            if (!_hasCleanTarget)
            {
                _cleanTarget = head;
                _cleanNormal = cleaner.LastCleanNormal;
                _hasCleanTarget = true;
            }
            if (cleaning)
            {
                _cleanTarget = Vector3.Lerp(_cleanTarget, cleaner.LastCleanPoint, follow);
                _cleanNormal = Vector3.Slerp(_cleanNormal, cleaner.LastCleanNormal, follow).normalized;
            }

            Vector3 cleanHead = ScrubHead(grip, forward, deltaTime);
            cleanHead = KeepHandleLength(grip, cleanHead, _cleanNormal, handleLength) + _cleanNormal * HEAD_LIFT;
            head = Vector3.Lerp(head, cleanHead, Mathf.SmoothStep(0f, 1f, _cleanWeight));
        }
        else
        {
            _hasCleanTarget = false;
        }

        Vector3 handleDirection = grip - head; // 걸레 머리 → 자루 끝
        if (handleDirection.sqrMagnitude < 1e-4f) return;
        handleDirection.Normalize();

        // 걸레 머리가 자루 둘레로 돌지 않게 들기 자세의 위쪽을 기준으로 방향을 잡는다 (앵커에 고정)
        Vector3 modelForward = Vector3.ProjectOnPlane(carryFrame * Vector3.up, handleDirection);
        if (modelForward.sqrMagnitude < 1e-4f) modelForward = Vector3.ProjectOnPlane(transform.forward, handleDirection);
        model.SetPositionAndRotation(head, Quaternion.LookRotation(modelForward.normalized, handleDirection));
    }

    /// <summary>
    /// 들고 다니는 자세의 걸레 머리 위치. 손 앵커 기준 carryPitch만큼 숙이고 carryYaw만큼 돌린 방향으로 손에 고정한다.
    /// 다른 들기 아이템처럼 앵커에 고정돼 시선을 따라 돌고, 그 밖의 보정은 하지 않는다.
    /// 아래를 아주 많이 볼 때만 자루가 수직을 넘어 몸 뒤로 넘어가지 않게 그 전에서 멈춘다.
    /// </summary>
    private Vector3 CarryHead(Vector3 grip, Quaternion carryFrame, float handleLength)
    {
        float length = Mathf.Max(0.1f, handleLength - carryHandleBehindGrip);
        Vector3 direction = carryFrame * Vector3.forward;

        // 손 앵커(이 아이템의 부모)가 시선 상하(RagdollPoser._carryFollowViewPitch)를 담고 있다. 양수 = 아래
        float viewPitch = Mathf.Asin(Mathf.Clamp(-transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
        float overshoot = viewPitch + carryPitch - MAX_CARRY_PITCH;
        if (overshoot > 0f) direction = Quaternion.AngleAxis(-overshoot, carryFrame * Vector3.right) * direction;

        return grip + direction * length;
    }

    /// <summary>닦는 자리 위에서 좌우로 크게, 앞뒤로 작게(두 배 빠르기) 흔들어 8자로 문지른다</summary>
    private Vector3 ScrubHead(Vector3 grip, Vector3 forward, float deltaTime)
    {
        _scrubPhase += deltaTime * scrubFrequency * 2f * Mathf.PI;

        Vector3 along = Vector3.ProjectOnPlane(_cleanTarget - grip, _cleanNormal);
        if (along.sqrMagnitude < 1e-4f) along = Vector3.ProjectOnPlane(forward, _cleanNormal);
        along.Normalize();
        Vector3 side = Vector3.Cross(_cleanNormal, along).normalized;
        return _cleanTarget + side * (Mathf.Sin(_scrubPhase) * scrubWidth) + along * (Mathf.Sin(_scrubPhase * 2f) * scrubPush);
    }

    /// <summary>
    /// 자루는 손 안에서 정해진 범위만큼만 미끄러지므로, 손에서 걸레 머리까지 거리를 그 범위로 맞춘다.
    /// 그냥 줄이거나 늘이면 머리가 바닥 속이나 허공으로 가므로, 목표 면 위에서 손에서 멀어지는(또는 가까워지는) 쪽으로 옮겨 면에 붙여 둔다.
    /// </summary>
    private Vector3 KeepHandleLength(Vector3 grip, Vector3 point, Vector3 normal, float handleLength)
    {
        float minLength = Mathf.Max(0.1f, handleLength - handleBehindGrip.y);
        float maxLength = Mathf.Max(minLength, handleLength - handleBehindGrip.x);

        Vector3 toPoint = point - grip;
        float distance = toPoint.magnitude;
        float length = Mathf.Clamp(distance, minLength, maxLength);
        if (Mathf.Abs(length - distance) < 1e-3f) return point;

        // 면 위에서 손에서 멀어지는 방향 u로 t만큼 옮긴 점이 손에서 length만큼 떨어지도록: |toPoint + u·t| = length
        Vector3 u = Vector3.ProjectOnPlane(toPoint, normal);
        if (u.sqrMagnitude < 1e-4f) return grip + toPoint.normalized * length;
        u.Normalize();

        float a = Vector3.Dot(toPoint, u);
        float discriminant = a * a - distance * distance + length * length;
        if (discriminant < 0f) return grip + toPoint.normalized * length; // 면이 자루보다 멀면 그쪽으로 뻗기만 한다
        return point + u * (-a + Mathf.Sqrt(discriminant));
    }

    private void RestoreModel()
    {
        model.localPosition = _restLocalPosition;
        model.localRotation = _restLocalRotation;
        model.localScale = _restLocalScale;
    }
}
