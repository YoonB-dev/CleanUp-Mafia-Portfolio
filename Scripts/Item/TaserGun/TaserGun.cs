using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(PickupItem))]
public class TaserGun : NetworkBehaviour, IUsableItem
{
    [Header("Gun Settings")]
    [SerializeField] private GameObject bulletPrefab;
    [SerializeField] private float fireRate = 0.5f;
    [SerializeField] private int maxAmmo = 3;
    [SerializeField] private float spawnForwardOffset = 0.6f; // 카메라 앞쪽 스폰 거리

    private readonly NetworkVariable<int> _currentAmmo = new(
        3,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    public int CurrentAmmo => _currentAmmo.Value;
    public int MaxAmmo => maxAmmo;

    // (현재 탄약, 최대 탄약) - 서버/클라이언트 모두에서 호출됨
    public event System.Action<int, int> AmmoChanged;

    private PickupItem _thisPickupItem;
    private float _nextFireTime;
    private float _serverNextFireTime;

    private void Awake()
    {
        _thisPickupItem = GetComponent<PickupItem>();
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            _currentAmmo.Value = maxAmmo;
        }
        _currentAmmo.OnValueChanged += HandleAmmoChanged;
    }

    public override void OnNetworkDespawn()
    {
        _currentAmmo.OnValueChanged -= HandleAmmoChanged;
    }

    private void HandleAmmoChanged(int previous, int current)
    {
        AmmoChanged?.Invoke(current, maxAmmo);
    }

    // PlayerInteraction으로부터 context와 본인의 playerCamera를 넘겨받음
    public void OnUse(InputAction.CallbackContext context, Camera playerCamera)
    {
        if (!IsOwner || !_thisPickupItem.IsHeld) return;

        if (context.performed)
        {
            Fire();
        }
    }

    private void Fire()
    {
        if (Time.time < _nextFireTime) return;
        if (_currentAmmo.Value <= 0) return;

        _nextFireTime = Time.time + fireRate;

        // 발사 위치와 방향은 서버가 든 사람의 시점으로 정하므로 요청만 보낸다
        RequestFireServerRpc();
    }

    [ServerRpc]
    private void RequestFireServerRpc(ServerRpcParams rpcParams = default)
    {
        if (!IsServer) return;

        if (_currentAmmo.Value <= 0 || !_thisPickupItem.IsHeld) return;

        // RPC를 보낸 클라이언트가 이 총을 든 사람인지 확인하고, 그 사람의 시점(서버에 동기화된 카메라)에서 발사한다
        var holder = _thisPickupItem.Holder;
        if (holder == null || holder.OwnerClientId != rpcParams.Receive.SenderClientId) return;
        if (holder.PlayerCamera == null) return;

        Ray ray = holder.PlayerCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0));
        Vector3 shootDirection = ray.direction;
        Vector3 spawnPos = ray.origin + shootDirection * spawnForwardOffset;

        // 1. 프리팹 및 총알 수량 기본 검증
        if (bulletPrefab == null)
        {
            Debug.LogError("[TaserGun] bulletPrefab이 인스펙터에 할당되지 않았습니다!");
            return;
        }
        // 서버 측 패킷 연사 검증
        if (Time.time < _serverNextFireTime) return;
        _serverNextFireTime = Time.time + fireRate;

        _currentAmmo.Value--;

        // 서버에서 총알 생성 및 네트워크 스폰
        Quaternion spawnRot = Quaternion.LookRotation(shootDirection);
        GameObject bulletObj = Instantiate(bulletPrefab, spawnPos, spawnRot);

        if (!bulletObj.TryGetComponent<NetworkObject>(out var bulletNetObj))
        {
            Debug.LogError("[TaserGun] bulletPrefab에 NetworkObject가 없습니다.");
            Destroy(bulletObj);
            return;
        }

        if (bulletObj.TryGetComponent<TaserBullet>(out var bullet))
        {
            ulong senderClientId = rpcParams.Receive.SenderClientId;
            if (NetworkManager.Singleton.ConnectedClients.TryGetValue(senderClientId, out var client) && client.PlayerObject != null)
            {
                bullet.Init(client.PlayerObject.NetworkObjectId);
            }
            else
            {
                // 예외 케이스 처리: TaserGun 자체의 NetworkObjectId 사용
                bullet.Init(NetworkObject.NetworkObjectId);
            }
        }
        bulletNetObj.Spawn();
    }
}