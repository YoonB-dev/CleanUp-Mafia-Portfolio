using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 아이템 모델을 투명 배경으로 찍어 인벤토리 아이콘을 만들고 ItemData에 연결
/// </summary>
public static class ItemIconGenerator
{
    private const string OUTPUT_FOLDER = "Assets/TEXTURE/Icons";
    private const int SIZE = 256;
    private const float PADDING = 1.1f;
    private const float ELONGATED_RATIO = 3f;
    private static readonly Quaternion VIEW = Quaternion.Euler(25f, 145f, 0f);

    private const string PAINT_GUN_PREFAB = "Assets/Prefab/Item/PaintGun.prefab";
    private const string SHREDDER_PREFAB = "Assets/Prefab/Item/TrashCrusher.prefab";
    private const string SHREDDER_ICON = "TrashCrusher";
    private const string INVENTORY_PREFAB = "Assets/Prefab/UI/CanvasInventory.prefab";
    private const string PAINT_GUN_SLOT = "Slot4Mafia";
    private const string TASER_BULLET_MODEL = "Assets/Models/SM_Taser_Bullet.fbx";
    private const string TASER_BULLET_ICON = "TaserBullet";
    private const string BULLET_ICON_PATH = "BulletCount/BulletIcon"; // 인벤토리 UI의 테이저 탄약 표시 그림
    private const float BULLET_LIGHTEN = 3.5f;
    private const string BOX_AREA_PREFAB = "Assets/Prefab/BoxArea.prefab"; // 박스를 놓는 지정 구역 (팔레트)
    private const string BOX_PREFAB = "Assets/Prefab/Item/Box.prefab";
    private const string BOX_AREA_ICON = "BoxArea";
    private const float BOX_AREA_BOX_SCALE = 1.8f;
    private const string VOTE_BUTTON_PREFAB = "Assets/Prefab/Maps/VoteButton.prefab";
    private const string VOTE_BUTTON_ICON = "VoteButton"; // 총알이 짙은 남색이라 어두운 칸 배경에서 보이게 밝힌다

    [MenuItem("Tools/Item Icons/Generate")]
    private static void Generate()
    {
        Directory.CreateDirectory(OUTPUT_FOLDER);

        Scene scene = EditorSceneManager.NewPreviewScene();
        Camera camera = CreateStudio(scene);

        foreach (string guid in AssetDatabase.FindAssets("t:Prefab"))
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
            if (!prefab.TryGetComponent(out PickupItem pickup)) continue;

            ItemData data = new SerializedObject(pickup).FindProperty("itemData").objectReferenceValue as ItemData;
            if (data == null) continue;

            SetIcon(data, Capture(camera, (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene), data.name));
        }

        foreach (string guid in AssetDatabase.FindAssets("t:TrashData"))
        {
            TrashData data = AssetDatabase.LoadAssetAtPath<TrashData>(AssetDatabase.GUIDToAssetPath(guid));
            GameObject model = new GameObject(data.name, typeof(MeshFilter), typeof(MeshRenderer));
            SceneManager.MoveGameObjectToScene(model, scene);
            model.GetComponent<MeshFilter>().sharedMesh = data.trashMesh;
            model.GetComponent<MeshRenderer>().sharedMaterial = data.trashMaterial;

            SetIcon(data, Capture(camera, model, data.name));
        }

        GameObject paintGun = AssetDatabase.LoadAssetAtPath<GameObject>(PAINT_GUN_PREFAB);
        SetPaintGunSlotIcon(Capture(camera, (GameObject)PrefabUtility.InstantiatePrefab(paintGun, scene), paintGun.name));

        CaptureShredder(scene, camera);
        SetBulletIcon(CaptureTaserBullet(scene, camera));
        CaptureBoxArea(scene, camera);
        CaptureVoteButton(scene, camera);

        EditorSceneManager.ClosePreviewScene(scene);
        AssetDatabase.SaveAssets();
    }

    /// <summary>파쇄기 아이콘만 다시 찍는다. 다른 아이콘과 인벤토리 프리팹은 건드리지 않는다</summary>
    [MenuItem("Tools/Item Icons/Generate Shredder")]
    public static void GenerateShredder()
    {
        Directory.CreateDirectory(OUTPUT_FOLDER);

        Scene scene = EditorSceneManager.NewPreviewScene();
        CaptureShredder(scene, CreateStudio(scene));
        EditorSceneManager.ClosePreviewScene(scene);
    }

    /// <summary>박스 지정 구역(팔레트 위에 박스) 그림만 다시 찍는다. 작업 지시서의 박스 과제 옆에 들어간다</summary>
    [MenuItem("Tools/Item Icons/Generate Box Area")]
    public static Sprite GenerateBoxArea()
    {
        Directory.CreateDirectory(OUTPUT_FOLDER);

        Scene scene = EditorSceneManager.NewPreviewScene();
        Sprite icon = CaptureBoxArea(scene, CreateStudio(scene));
        EditorSceneManager.ClosePreviewScene(scene);
        return icon;
    }

    /// <summary>긴급 투표 버튼 그림만 다시 찍는다. 작업 지시서의 긴급 투표 쪽에 들어간다</summary>
    [MenuItem("Tools/Item Icons/Generate Vote Button")]
    public static Sprite GenerateVoteButton()
    {
        Directory.CreateDirectory(OUTPUT_FOLDER);

        Scene scene = EditorSceneManager.NewPreviewScene();
        Sprite icon = CaptureVoteButton(scene, CreateStudio(scene));
        EditorSceneManager.ClosePreviewScene(scene);
        return icon;
    }

    private static Sprite CaptureVoteButton(Scene scene, Camera camera)
    {
        GameObject button = AssetDatabase.LoadAssetAtPath<GameObject>(VOTE_BUTTON_PREFAB);
        return button != null ? Capture(camera, (GameObject)PrefabUtility.InstantiatePrefab(button, scene), VOTE_BUTTON_ICON) : null;
    }

    // 팔레트만 있으면 무엇을 놓는 곳인지 알기 어려워서, 박스 하나를 올린 모습으로 찍는다
    private static Sprite CaptureBoxArea(Scene scene, Camera camera)
    {
        GameObject areaPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(BOX_AREA_PREFAB);
        GameObject boxPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(BOX_PREFAB);
        if (areaPrefab == null || boxPrefab == null) return null;

        GameObject root = new GameObject(BOX_AREA_ICON);
        SceneManager.MoveGameObjectToScene(root, scene);
        GameObject area = (GameObject)PrefabUtility.InstantiatePrefab(areaPrefab, scene);
        area.transform.SetParent(root.transform, false);

        Bounds areaBounds = BoundsOf(area);
        GameObject box = (GameObject)PrefabUtility.InstantiatePrefab(boxPrefab, scene);
        box.transform.SetParent(root.transform, false);
        box.transform.rotation = Quaternion.Euler(0f, 15f, 0f);
        // 실제 비율대로면 작은 칸에서 박스가 점처럼 보여서, 무엇을 놓는 곳인지 보이게 키운다
        box.transform.localScale *= BOX_AREA_BOX_SCALE;
        Bounds boxBounds = BoundsOf(box);
        box.transform.position += new Vector3(areaBounds.center.x - boxBounds.center.x, areaBounds.max.y - boxBounds.min.y, areaBounds.center.z - boxBounds.center.z);

        return Capture(camera, root, BOX_AREA_ICON);
    }

    private static Bounds BoundsOf(GameObject model)
    {
        Renderer[] renderers = model.GetComponentsInChildren<Renderer>().Where(r => r is MeshRenderer || r is SkinnedMeshRenderer).ToArray();
        Bounds bounds = renderers[0].bounds;
        foreach (Renderer renderer in renderers) bounds.Encapsulate(renderer.bounds);
        return bounds;
    }

    /// <summary>테이저건 총알 아이콘만 다시 찍어 인벤토리 탄약 표시에 넣는다</summary>
    [MenuItem("Tools/Item Icons/Generate Taser Bullet")]
    public static void GenerateTaserBullet()
    {
        Directory.CreateDirectory(OUTPUT_FOLDER);

        Scene scene = EditorSceneManager.NewPreviewScene();
        SetBulletIcon(CaptureTaserBullet(scene, CreateStudio(scene)));
        EditorSceneManager.ClosePreviewScene(scene);
    }

    // 총알은 아이템이 아니라 테이저건 탄약 표시에만 쓰인다
    private static Sprite CaptureTaserBullet(Scene scene, Camera camera)
    {
        GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(TASER_BULLET_MODEL);
        if (model == null) return null;
        Sprite icon = Capture(camera, (GameObject)PrefabUtility.InstantiatePrefab(model, scene), TASER_BULLET_ICON);
        return icon != null ? Lighten(AssetDatabase.GetAssetPath(icon), BULLET_LIGHTEN) : null;
    }

    /// <summary>
    /// 짙은 색 모델은 어두운 칸 배경에 묻히므로 밝게 띄운다. 어두운 곳일수록 많이 올려 음영은 남긴다 (1 - (1-c)^power)
    /// </summary>
    private static Sprite Lighten(string path, float power)
    {
        Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        texture.LoadImage(File.ReadAllBytes(path));
        Color[] pixels = texture.GetPixels();
        for (int i = 0; i < pixels.Length; i++)
        {
            Color c = pixels[i];
            pixels[i] = new Color(1f - Mathf.Pow(1f - c.r, power), 1f - Mathf.Pow(1f - c.g, power), 1f - Mathf.Pow(1f - c.b, power), c.a);
        }
        texture.SetPixels(pixels);
        File.WriteAllBytes(path, texture.EncodeToPNG());
        Object.DestroyImmediate(texture);

        AssetDatabase.ImportAsset(path);
        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }

    private static void SetBulletIcon(Sprite icon)
    {
        if (icon == null) return;

        GameObject root = PrefabUtility.LoadPrefabContents(INVENTORY_PREFAB);
        Transform target = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(child => child.name == "BulletCount")?.Find("BulletIcon");
        if (target != null && target.TryGetComponent(out Image image))
        {
            image.sprite = icon;
            PrefabUtility.SaveAsPrefabAsset(root, INVENTORY_PREFAB);
        }
        else
        {
            Debug.LogWarning($"[ItemIconGenerator] {INVENTORY_PREFAB}에 {BULLET_ICON_PATH}가 없어 총알 아이콘을 넣지 못했습니다.");
        }
        PrefabUtility.UnloadPrefabContents(root);
    }

    // 파쇄기는 줍는 아이템이 아니지만 작업 지시서의 물품 안내에 그림이 들어간다
    private static void CaptureShredder(Scene scene, Camera camera)
    {
        GameObject shredder = AssetDatabase.LoadAssetAtPath<GameObject>(SHREDDER_PREFAB);
        if (shredder != null) Capture(camera, (GameObject)PrefabUtility.InstantiatePrefab(shredder, scene), SHREDDER_ICON);
    }

    private static Camera CreateStudio(Scene scene)
    {
        GameObject studio = new GameObject("Studio", typeof(Camera));
        SceneManager.MoveGameObjectToScene(studio, scene);

        Camera camera = studio.GetComponent<Camera>();
        camera.scene = scene;
        camera.orthographic = true;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.clear;
        camera.allowHDR = false;
        camera.transform.rotation = VIEW;

        // 프리뷰 씬은 환경광이 없어 반대편에서 약하게 채워야 그늘진 면이 까맣게 뭉개지지 않는다
        AddLight(scene, Quaternion.Euler(50f, 150f, 0f), 1.2f);
        AddLight(scene, Quaternion.Euler(-20f, -30f, 0f), 0.5f);

        return camera;
    }

    private static void AddLight(Scene scene, Quaternion rotation, float intensity)
    {
        GameObject lightObject = new GameObject("Light", typeof(Light));
        SceneManager.MoveGameObjectToScene(lightObject, scene);
        lightObject.transform.rotation = rotation;

        Light light = lightObject.GetComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = intensity;
    }

    private static void SetIcon(ItemData data, Sprite icon)
    {
        if (icon == null) return;

        data.icon = icon;
        EditorUtility.SetDirty(data);
    }

    // 페인트건은 줍는 아이템이 아니라 마피아 4번 슬롯에 고정돼 있어 ItemData 대신 인벤토리 UI에 직접 넣는다
    private static void SetPaintGunSlotIcon(Sprite icon)
    {
        if (icon == null) return;

        GameObject root = PrefabUtility.LoadPrefabContents(INVENTORY_PREFAB);
        Transform slot = root.GetComponentsInChildren<Transform>(true).First(child => child.name == PAINT_GUN_SLOT);
        slot.Find("ItemImage").GetComponent<Image>().sprite = icon;
        PrefabUtility.SaveAsPrefabAsset(root, INVENTORY_PREFAB);
        PrefabUtility.UnloadPrefabContents(root);
    }

    // 월드 축 상자는 기울어진 모델을 헐겁게 감싸므로 렌더러마다 자기 축 상자의 꼭짓점을 모은다
    private static List<Vector3> Corners(GameObject model)
    {
        List<Vector3> corners = new();
        foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>())
        {
            if (renderer is not MeshRenderer && renderer is not SkinnedMeshRenderer) continue;

            Bounds local = renderer.localBounds;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = local.center + Vector3.Scale(local.extents, new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1) * 2f - Vector3.one);
                corners.Add(renderer.localToWorldMatrix.MultiplyPoint3x4(corner));
            }
        }
        return corners;
    }

    private static Bounds Enclose(List<Vector3> points, Quaternion rotation)
    {
        Bounds bounds = new Bounds(rotation * points[0], Vector3.zero);
        foreach (Vector3 point in points)
        {
            bounds.Encapsulate(rotation * point);
        }
        return bounds;
    }

    private static Sprite Capture(Camera camera, GameObject model, string iconName)
    {
        List<Vector3> corners = Corners(model);
        if (corners.Count == 0)
        {
            Debug.LogWarning($"[ItemIconGenerator] {iconName}: 찍을 모델이 없어 건너뜀");
            Object.DestroyImmediate(model);
            return null;
        }

        // 막대처럼 긴 물건은 시선과 수직인 화면 대각선으로 눕혀야 짧아 보이지 않고 칸을 채운다
        Bounds world = Enclose(corners, Quaternion.identity);
        Vector3 size = world.size;
        int longest = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
        if (size[longest] > Mathf.Max(size[(longest + 1) % 3], size[(longest + 2) % 3]) * ELONGATED_RATIO)
        {
            Vector3 axis = Vector3.zero;
            axis[longest] = 1f;
            model.transform.rotation = Quaternion.FromToRotation(axis, VIEW * new Vector3(-1f, -1f, 0f).normalized) * model.transform.rotation;
            corners = Corners(model);
        }

        Bounds view = Enclose(corners, Quaternion.Inverse(VIEW));
        camera.transform.position = VIEW * (view.center - Vector3.forward * (view.extents.z + 1f));
        camera.orthographicSize = Mathf.Max(view.extents.x, view.extents.y) * PADDING;
        camera.nearClipPlane = 0.01f;
        camera.farClipPlane = view.size.z + 2f;

        RenderTexture target = new RenderTexture(SIZE, SIZE, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 4 };
        Texture2D texture = new Texture2D(SIZE, SIZE, TextureFormat.RGBA32, false);

        camera.targetTexture = target;
        // 막 불러온 머티리얼은 첫 렌더에서 텍스처가 아직 안 올라가 단색(노란색)으로 찍힐 때가 있어 한 번 미리 그린다
        camera.Render();
        camera.Render();
        RenderTexture.active = target;
        texture.ReadPixels(new Rect(0, 0, SIZE, SIZE), 0, 0);
        texture.Apply();
        RenderTexture.active = null;
        camera.targetTexture = null;

        string path = $"{OUTPUT_FOLDER}/{iconName}.png";
        File.WriteAllBytes(path, texture.EncodeToPNG());

        Object.DestroyImmediate(target);
        Object.DestroyImmediate(texture);
        Object.DestroyImmediate(model);

        AssetDatabase.ImportAsset(path);
        TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.alphaIsTransparency = true;
        importer.SaveAndReimport();

        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }
}
