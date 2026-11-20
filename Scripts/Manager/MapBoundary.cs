using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

/// <summary>
/// 맵의 활동 구역(울타리 안). 두 겹으로 막는다.
/// 1. 구역 테두리 바로 바깥에 아주 높고 두꺼운 투명 벽을 세운다. 점프·돌진(래그돌)·던진 물건이 울타리를 넘지 못한다.
///    벽은 Ignore Raycast 레이어라 몸과 물건은 막지만 줍기·페인트·카메라 같은 레이에는 걸리지 않는다.
/// 2. 서버가 주기적으로 플레이어와 물건 위치를 보고, 그래도 구역 밖에 있으면 안쪽으로 되돌린다.
/// 구역은 이 오브젝트 기준 XZ 사각형이다. 울타리 오브젝트들을 fenceRoot에 넣고 컨텍스트 메뉴 "울타리에 맞추기"로 맞출 수 있다.
/// </summary>
public class MapBoundary : MonoBehaviour
{
    [Header("구역 (월드 좌표, XZ)")]
    [Tooltip("구역 가운데. y는 벽 높이의 기준(바닥 높이)")]
    [SerializeField] private Vector3 center;
    [Tooltip("구역 크기. x·z만 쓴다. 울타리 안쪽 면에 맞춘다")]
    [SerializeField] private Vector2 size = new(100f, 100f);
    [Tooltip("\"울타리에 맞추기\"가 읽을 울타리 묶음 (자식 콜라이더 전체로 구역을 정한다)")]
    [SerializeField] private Transform fenceRoot;

    [Header("투명 벽")]
    [Tooltip("벽이 바닥(center.y)보다 아래로 내려가는 깊이와 위로 솟는 높이 (m)")]
    [SerializeField] private Vector2 wallDepthHeight = new(30f, 80f);
    [Tooltip("벽 두께 (m). 빠르게 날아오는 래그돌·물건이 뚫지 못할 만큼")]
    [SerializeField, Min(0.5f)] private float wallThickness = 4f;

    [Header("서버 되돌리기")]
    [Tooltip("구역 밖으로 이만큼 넘어가면 되돌린다 (m)")]
    [SerializeField, Min(0f)] private float tolerance = 1f;
    [Tooltip("되돌릴 때 테두리에서 안쪽으로 들여놓는 거리 (m)")]
    [SerializeField, Min(0f)] private float returnInset = 1.5f;
    [Tooltip("검사 간격 (초)")]
    [SerializeField, Min(0.05f)] private float checkInterval = 0.25f;

    private const string WALL_LAYER = "Ignore Raycast";
    private float _nextCheck;

    private float MinX => center.x - size.x * 0.5f;
    private float MaxX => center.x + size.x * 0.5f;
    private float MinZ => center.z - size.y * 0.5f;
    private float MaxZ => center.z + size.y * 0.5f;

    private void Awake()
    {
        BuildWalls();
    }

    private void BuildWalls()
    {
        int layer = LayerMask.NameToLayer(WALL_LAYER);
        float height = wallDepthHeight.x + wallDepthHeight.y;
        float y = center.y + (wallDepthHeight.y - wallDepthHeight.x) * 0.5f;
        float t = wallThickness;

        // 북·남 벽은 모서리를 덮도록 두께만큼 더 길게
        CreateWall("BoundaryWall_N", new Vector3(center.x, y, MaxZ + t * 0.5f), new Vector3(size.x + t * 2f, height, t), layer);
        CreateWall("BoundaryWall_S", new Vector3(center.x, y, MinZ - t * 0.5f), new Vector3(size.x + t * 2f, height, t), layer);
        CreateWall("BoundaryWall_E", new Vector3(MaxX + t * 0.5f, y, center.z), new Vector3(t, height, size.y), layer);
        CreateWall("BoundaryWall_W", new Vector3(MinX - t * 0.5f, y, center.z), new Vector3(t, height, size.y), layer);
    }

    private void CreateWall(string wallName, Vector3 position, Vector3 wallSize, int layer)
    {
        var wall = new GameObject(wallName);
        wall.transform.SetParent(transform, false);
        wall.transform.SetPositionAndRotation(position, Quaternion.identity);
        wall.transform.localScale = Vector3.one;
        if (layer >= 0) wall.layer = layer;
        wall.isStatic = true;
        wall.AddComponent<BoxCollider>().size = wallSize;
    }

    private void Update()
    {
        NetworkManager network = NetworkManager.Singleton;
        if (network == null || !network.IsServer || Time.time < _nextCheck) return;
        _nextCheck = Time.time + checkInterval;

        foreach (NetworkClient client in network.ConnectedClientsList)
        {
            if (client.PlayerObject != null) KeepPlayerInside(client.PlayerObject);
        }
        foreach (PickupItem item in FindObjectsByType<PickupItem>(FindObjectsSortMode.None))
        {
            if (item.IsSpawned && !item.IsHeld) KeepObjectInside(item.gameObject);
        }
        foreach (DraggableObject draggable in FindObjectsByType<DraggableObject>(FindObjectsSortMode.None))
        {
            if (draggable.IsSpawned && !draggable.IsBeingDragged) KeepObjectInside(draggable.gameObject);
        }
    }

    private bool IsOutside(Vector3 p) =>
        p.x < MinX - tolerance || p.x > MaxX + tolerance || p.z < MinZ - tolerance || p.z > MaxZ + tolerance;

    private Vector3 ClampInside(Vector3 p) => new(
        Mathf.Clamp(p.x, MinX + returnInset, MaxX - returnInset),
        p.y,
        Mathf.Clamp(p.z, MinZ + returnInset, MaxZ - returnInset));

    private void KeepPlayerInside(NetworkObject player)
    {
        if (!IsOutside(player.transform.position)) return;
        // 쓰러져 날아가는 중이면 몸(래그돌)이 일어난 뒤에 판단한다. 벽이 먼저 막아 준다
        if (player.TryGetComponent(out PlayerKnockdown knockdown) && knockdown.IsDown) return;
        if (!player.TryGetComponent(out PlayerMovement movement)) return;

        Vector3 target = ClampInside(player.transform.position) + Vector3.up * 0.5f;
        Debug.Log($"[MapBoundary] {player.name}이(가) 구역 밖({player.transform.position})에 있어 안으로 되돌립니다.");
        movement.ServerTeleport(target, player.transform.eulerAngles.y);
    }

    private void KeepObjectInside(GameObject obj)
    {
        Vector3 position = obj.transform.position;
        if (!IsOutside(position)) return;

        Vector3 target = ClampInside(position) + Vector3.up * 1f;
        if (obj.TryGetComponent(out Rigidbody body))
        {
            if (!body.isKinematic)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            body.position = target;
        }
        obj.transform.position = target;
        if (obj.TryGetComponent(out NetworkTransform networkTransform) && networkTransform.CanCommitToTransform)
        {
            networkTransform.Teleport(target, obj.transform.rotation, obj.transform.localScale);
        }
    }

#if UNITY_EDITOR
    [ContextMenu("울타리에 맞추기")]
    private void FitToFence()
    {
        if (fenceRoot == null) return;
        Collider[] colliders = fenceRoot.GetComponentsInChildren<Collider>();
        if (colliders.Length == 0) return;

        Bounds bounds = colliders[0].bounds;
        foreach (Collider c in colliders) bounds.Encapsulate(c.bounds);

        // 울타리 두께의 바깥 면이 아니라 안쪽 면에 맞춘다 (울타리 철망 바로 안쪽까지가 구역)
        float inner = Mathf.Min(colliders[0].bounds.size.x, colliders[0].bounds.size.z);
        UnityEditor.Undo.RecordObject(this, "Fit MapBoundary to fence");
        center = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
        size = new Vector2(bounds.size.x - inner * 2f, bounds.size.z - inner * 2f);
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = new Color(1f, 0.3f, 0.2f, 0.9f);
        Vector3 c = new(center.x, center.y + 1f, center.z);
        Gizmos.DrawWireCube(c, new Vector3(size.x, 2f, size.y));
        Gizmos.color = new Color(1f, 0.3f, 0.2f, 0.15f);
        float height = wallDepthHeight.x + wallDepthHeight.y;
        float y = center.y + (wallDepthHeight.y - wallDepthHeight.x) * 0.5f;
        Gizmos.DrawCube(new Vector3(center.x, y, MaxZ + wallThickness * 0.5f), new Vector3(size.x, height, wallThickness));
        Gizmos.DrawCube(new Vector3(center.x, y, MinZ - wallThickness * 0.5f), new Vector3(size.x, height, wallThickness));
        Gizmos.DrawCube(new Vector3(MaxX + wallThickness * 0.5f, y, center.z), new Vector3(wallThickness, height, size.y));
        Gizmos.DrawCube(new Vector3(MinX - wallThickness * 0.5f, y, center.z), new Vector3(wallThickness, height, size.y));
    }
#endif
}
