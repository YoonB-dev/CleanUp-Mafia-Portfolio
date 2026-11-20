using UnityEngine;

/// <summary>
/// 맵 씬마다 하나 두는 맵 정보. InGameScene의 매니저가 맵에 따라 달라지는 값(스폰 지점)을 여기서 가져간다.
/// </summary>
public class MapInfo : SceneSingleton<MapInfo>
{
    [Tooltip("플레이어 스폰 지점. 플레이어 피벗(캡슐 중심) 높이에 두고, 인원보다 적으면 돌려 쓴다")]
    [SerializeField] private Transform[] spawnPoints;
    [Tooltip("켜면 판마다 스폰 지점을 섞어서 배정한다. 끄면 목록 순서대로")]
    [SerializeField] private bool shuffleSpawnPoints = true;

    [Header("게임 종료 무대")]
    [Tooltip("게임이 끝나면 켜는 무대 묶음(무대 판·카메라·서는 자리). 평소에는 꺼 둔다. 비워 두면 무대 연출 없이 결과만 뜬다")]
    [SerializeField] private GameObject podium;
    [Tooltip("무대를 비추는 카메라. podium 안에 둔다")]
    [SerializeField] private Camera podiumCamera;
    [Tooltip("이긴 팀이 서는 무대 위 가운데. 플레이어 피벗 높이, 앞(파란 축)이 카메라 쪽")]
    [SerializeField] private Transform winnerSpot;
    [Tooltip("진 팀이 쓰러지는 무대 앞 가운데")]
    [SerializeField] private Transform loserSpot;
    [Tooltip("무대 위에 한 줄로 설 때 사람 사이 간격(m). 무대가 좁으면 두 줄로 선다")]
    [SerializeField] private float winnerSpacing = 2.4f;
    [SerializeField] private int winnersPerRow = 5;
    [Tooltip("진 팀이 흩어져 떨어지는 범위(가로, 세로 m)")]
    [SerializeField] private Vector2 loserArea = new(10f, 3f);
    [Tooltip("무대가 켜질 때 끌 물건(무대·쓰러질 자리와 겹치는 가로등, 화분 등)")]
    [SerializeField] private GameObject[] hideDuringPodium;

    public bool HasPodium => podium != null && winnerSpot != null && loserSpot != null;

    private int[] _order;

    /// <summary>index번째 플레이어가 나올 스폰 지점의 위치와 방향</summary>
    public Pose GetSpawnPose(int index)
    {
        if (spawnPoints == null || spawnPoints.Length == 0) return new Pose(transform.position, transform.rotation);

        _order ??= MakeOrder();
        Transform point = spawnPoints[_order[index % _order.Length]];
        return point != null ? new Pose(point.position, point.rotation) : new Pose(transform.position, transform.rotation);
    }

    /// <summary>무대를 켜고 화면을 무대 카메라로 바꾼다. 모두의 화면에서 부른다</summary>
    /// <param name="playerCamera">꺼 둘 내 1인칭 카메라</param>
    public void ShowPodium(Camera playerCamera)
    {
        if (!HasPodium) return;

        podium.SetActive(true);
        if (hideDuringPodium != null)
        {
            foreach (GameObject hidden in hideDuringPodium)
            {
                if (hidden != null) hidden.SetActive(false);
            }
        }
        if (podiumCamera == null) return;

        podiumCamera.enabled = true;
        if (playerCamera != null) playerCamera.enabled = false;
    }

    /// <summary>이긴 팀 index번째가 설 자리. 가운데부터 좌우로 번갈아 채우고, 한 줄이 차면 뒷줄로 간다</summary>
    public Pose GetWinnerPose(int index)
    {
        int row = index / winnersPerRow;
        int col = index % winnersPerRow;
        // 뒷줄은 반 칸 비켜 서서 앞줄 머리 사이로 보이게 한다
        float side = (col + 1) / 2 * (col % 2 == 0 ? 1f : -1f) + (row % 2) * 0.5f;
        Vector3 position = winnerSpot.position + winnerSpot.right * (side * winnerSpacing) - winnerSpot.forward * (row * winnerSpacing);
        return new Pose(position, winnerSpot.rotation);
    }

    /// <summary>진 팀이 떨어질 자리. 무대 앞에 흩어진다</summary>
    public Pose GetLoserPose()
    {
        Vector3 offset = loserSpot.right * Random.Range(-0.5f, 0.5f) * loserArea.x + loserSpot.forward * Random.Range(-0.5f, 0.5f) * loserArea.y;
        return new Pose(loserSpot.position + offset, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
    }

    // 맵 씬은 판마다 새로 로드되므로 한 판에 한 번 섞인다. 지점이 인원보다 많으면 판마다 쓰이는 지점도 달라진다
    private int[] MakeOrder()
    {
        var order = new int[spawnPoints.Length];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        if (!shuffleSpawnPoints) return order;

        for (int i = order.Length - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        return order;
    }

#if UNITY_EDITOR
    private static readonly Color GizmoColor = new(0.2f, 0.85f, 1f, 0.9f);
    private static readonly Vector3[] CapsuleSides = { Vector3.right, Vector3.left, Vector3.forward, Vector3.back };
    private const float CapsuleRadius = 0.25f;   // 플레이어 CharacterController와 같은 크기
    private const float CapsuleHeight = 1.8f;

    // 씬 뷰에서 스폰 지점을 플레이어 크기의 캡슐, 바라보는 방향 화살표, 번호로 보여 준다.
    // 목록(spawnPoints)에 넣은 것만 그리므로, 복제해 놓고 목록에 안 넣은 지점은 안 보인다
    private void OnDrawGizmos()
    {
        if (spawnPoints == null) return;

        Gizmos.color = GizmoColor;
        Vector3 half = Vector3.up * (CapsuleHeight * 0.5f - CapsuleRadius);

        for (int i = 0; i < spawnPoints.Length; i++)
        {
            Transform point = spawnPoints[i];
            if (point == null) continue;

            Vector3 center = point.position;
            Gizmos.DrawWireSphere(center + half, CapsuleRadius);
            Gizmos.DrawWireSphere(center - half, CapsuleRadius);
            foreach (Vector3 side in CapsuleSides)
                Gizmos.DrawLine(center + half + side * CapsuleRadius, center - half + side * CapsuleRadius);

            Vector3 forward = Vector3.ProjectOnPlane(point.forward, Vector3.up).normalized;
            if (forward != Vector3.zero)
            {
                Vector3 eye = center + Vector3.up * 0.5f;
                Vector3 tip = eye + forward * 0.8f;
                Vector3 wing = Vector3.Cross(Vector3.up, forward) * 0.15f;
                Gizmos.DrawLine(eye, tip);
                Gizmos.DrawLine(tip, tip - forward * 0.25f + wing);
                Gizmos.DrawLine(tip, tip - forward * 0.25f - wing);
            }

            UnityEditor.Handles.Label(center + Vector3.up * (CapsuleHeight * 0.5f + 0.3f), (i + 1).ToString());
        }
    }
#endif
}
