using Unity.Netcode;
using UnityEngine;

public class TaserBullet : NetworkBehaviour
{
    [SerializeField] private float speed = 25f;
    [SerializeField] private float lifeTime = 3f;
    [SerializeField] private float slowDuration = 3f; // 감전 지속 시간
    [SerializeField] private float slowFactor = 0.3f;   // 이동 속도 비율 (30%로 감속)
    [Tooltip("충돌 판정 반경 (미터). 총알 콜라이더 크기와 비슷하게")]
    [SerializeField] private float hitRadius = 0.05f;
    [Tooltip("벽/바닥 등 플레이어가 아닌 곳에 맞았을 때 띄울 이펙트 (예: Prefab/VFX/TaserHit)")]
    [SerializeField] private GameObject hitEffectPrefab;
    [Tooltip("이펙트를 정리할 때까지의 시간(초)")]
    [SerializeField] private float hitEffectLifetime = 1f;

    private ulong _shooterNetId;
    private readonly RaycastHit[] _hits = new RaycastHit[16];

    public void Init(ulong shooterNetId)
    {
        _shooterNetId = shooterNetId;
    }

    private void Start()
    {
        if (IsServer)
        {
            Destroy(gameObject, lifeTime);
        }
    }

    private void Update()
    {
        if (!IsServer || !IsSpawned) return;

        // 이번 프레임에 이동할 구간을 미리 쓸어서 맞는 게 있으면 그 자리에서 처리한다.
        // 트리거 이벤트는 Rigidbody 없는 벽/바닥과는 발생하지 않고, 빠른 탄은 얇은 벽을 건너뛰기 때문
        float step = speed * Time.deltaTime;
        if (TryFindHit(step, out Collider hitCollider, out Vector3 hitPoint, out Vector3 hitNormal))
        {
            HandleHit(hitCollider, hitPoint, hitNormal);
            return;
        }

        // 전방으로 직진 이동
        transform.position += transform.forward * step;
    }

    /// <summary>
    /// 이동 구간에서 가장 가까운 유효 충돌을 찾는다. 쏜 본인과, 플레이어가 아닌 트리거(구역 판정 등)는 건너뛴다
    /// </summary>
    private bool TryFindHit(float step, out Collider hitCollider, out Vector3 hitPoint, out Vector3 hitNormal)
    {
        hitCollider = null;
        hitPoint = default;
        hitNormal = -transform.forward;
        float nearest = float.PositiveInfinity;

        int count = Physics.SphereCastNonAlloc(transform.position, hitRadius, transform.forward, _hits, step,
            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide);
        for (int i = 0; i < count; i++)
        {
            RaycastHit hit = _hits[i];
            if (hit.collider == null || hit.distance >= nearest) continue;
            if (IsShooter(hit.collider)) continue;
            if (hit.collider.isTrigger && PlayerOf(hit.collider) == null) continue;

            nearest = hit.distance;
            hitCollider = hit.collider;
            // 시작 시점에 이미 겹쳐 있으면 point가 비어 있으므로 현재 위치를 쓴다
            hitPoint = hit.distance <= 0f ? transform.position : hit.point;
            hitNormal = hit.distance <= 0f ? -transform.forward : hit.normal;
        }
        return hitCollider != null;
    }

    private bool IsShooter(Collider other)
    {
        // 쏜 사람의 몸(래그돌 팔·손 포함)이면 건너뛴다. 총구 바로 앞에 든 손이 있어서 안 그러면 쏘자마자 자기 손에 맞고 사라진다
        PlayerInteraction player = PlayerOf(other);
        if (player != null) return player.NetworkObjectId == _shooterNetId;

        // 플레이어가 아닌 경우(쏜 주체가 총 자체인 예외 상황): 최상위 NetworkObject로 비교
        NetworkObject hitNetObj = other.transform.root.GetComponent<NetworkObject>();
        return hitNetObj != null && hitNetObj.NetworkObjectId == _shooterNetId;
    }

    /// <summary>
    /// 콜라이더가 속한 플레이어. 래그돌은 흔들림을 막으려고 Player에서 떼어 따로 두므로(RagdollDriver),
    /// 부모를 따라가서 못 찾으면 래그돌이 기억하는 원래 플레이어로 찾는다
    /// </summary>
    private static PlayerInteraction PlayerOf(Collider other)
    {
        PlayerInteraction player = other.GetComponentInParent<PlayerInteraction>();
        if (player != null) return player;

        RagdollDriver ragdoll = other.GetComponentInParent<RagdollDriver>();
        return ragdoll != null && ragdoll.PlayerRoot != null ? ragdoll.PlayerRoot.GetComponent<PlayerInteraction>() : null;
    }

    private void HandleHit(Collider other, Vector3 hitPoint, Vector3 hitNormal)
    {
        // 1. 맞은 플레이어 탐색. 몸통 캡슐뿐 아니라 래그돌 팔다리에 맞아도 그 플레이어가 맞은 것으로 친다
        PlayerInteraction targetPlayer = PlayerOf(other);

        if (targetPlayer != null)
        {
            Debug.Log($"[TaserBullet] SUCCESS Hit player: {targetPlayer.name} (IsHost: {targetPlayer.IsHost})");

            // 대상 플레이어에게 감전(이동 속도 저하) 적용
            if (targetPlayer.TryGetComponent<PlayerMovement>(out var movement))
            {
                movement.ApplySlowServer(slowDuration, slowFactor);
            }

            // 맞은 본인이 감전된 걸 바로 알 수 있게 화면에 전기 효과
            if (targetPlayer.TryGetComponent<TaserScreenEffect>(out var screenEffect))
            {
                screenEffect.PlayServer(slowDuration);
            }

            // 맞은 지점에서 가장 가까운 본에 탄이 박힌 연출 (감전이 풀리면 PlayerMovement가 제거)
            if (targetPlayer.TryGetComponent<PlayerTaserStuck>(out var stuck))
            {
                stuck.AttachServer(other.ClosestPoint(hitPoint), transform.rotation);
            }
        }
        else
        {
            // 플레이어는 탄 박힘 연출이 있으므로, 그 외 표면에 맞았을 때만 스파크를 띄운다
            PlayHitEffectClientRpc(hitPoint, hitNormal);
        }

        // 2. 플레이어든 벽/바닥/물건이든 맞으면 총알 제거 (위 RPC가 먼저 전달된 뒤 디스폰된다)
        if (NetworkObject != null && NetworkObject.IsSpawned)
        {
            NetworkObject.Despawn();
        }
    }

    [ClientRpc]
    private void PlayHitEffectClientRpc(Vector3 point, Vector3 normal)
    {
        if (hitEffectPrefab == null) return;

        // 총알과 별개로 남아야 하므로 부모 없이 띄우고 시간이 지나면 정리한다
        GameObject effect = Instantiate(hitEffectPrefab, point + normal * 0.01f, Quaternion.LookRotation(normal));
        Destroy(effect, hitEffectLifetime);
    }
}