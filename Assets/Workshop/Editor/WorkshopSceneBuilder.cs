using System.IO;
using Unity.Scenes;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Workshop.Editor
{
    /// <summary>
    /// Builds the workshop's materials, prefabs, scene and subscene from scratch.
    ///
    /// This exists so the starter project is reproducible: if the scene is ever
    /// corrupted or needs regenerating for a new cohort, this rebuilds it exactly
    /// rather than relying on someone remembering which values to type where.
    /// Attendees never run it.
    /// </summary>
    public static class WorkshopSceneBuilder
    {
        private const string ArtPath = "Assets/Workshop/Art";
        private const string ScenePath = "Assets/Scenes/Workshop.unity";
        private const string SubScenePath = "Assets/Scenes/Workshop_SubScene.unity";

        [MenuItem("Workshop/Rebuild Starter Scene")]
        public static void Build()
        {
            Directory.CreateDirectory(ArtPath);
            Directory.CreateDirectory("Assets/Scenes");

            var playerMat = CreateMaterial("PlayerMat", new Color(0.95f, 0.95f, 0.95f), 1.5f);
            var enemyMat = CreateMaterial("EnemyMat", new Color(0.85f, 0.15f, 0.15f), 0f);
            var bulletMat = CreateMaterial("BulletMat", new Color(1f, 0.9f, 0.2f), 3f);

            var playerPrefab = CreatePlayerPrefab(playerMat);
            var enemyPrefab = CreateEnemyPrefab(enemyMat);
            var bulletPrefab = CreateBulletPrefab(bulletMat);

            BuildScenes(playerPrefab, enemyPrefab, bulletPrefab);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[Workshop] Starter scene rebuilt.");
        }

        private static Material CreateMaterial(string name, Color color, float emission)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            var material = new Material(shader);
            material.SetColor("_BaseColor", color);

            if (emission > 0f)
            {
                material.EnableKeyword("_EMISSION");
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                material.SetColor("_EmissionColor", color * emission);
            }

            var path = $"{ArtPath}/{name}.mat";
            AssetDatabase.CreateAsset(material, path);
            return AssetDatabase.LoadAssetAtPath<Material>(path);
        }

        private static GameObject CreatePrimitive(
            PrimitiveType type, string name, Vector3 scale, Material material)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = material;

            // No physics in this project - the colliders primitives ship with are dead
            // weight, and at 50k instances they are not free.
            Object.DestroyImmediate(go.GetComponent<Collider>());
            return go;
        }

        private static GameObject SaveAsPrefab(GameObject go, string name)
        {
            var path = $"{ArtPath}/{name}.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            return prefab;
        }

        private static GameObject CreatePlayerPrefab(Material material)
        {
            var go = CreatePrimitive(PrimitiveType.Capsule, "Player", Vector3.one, material);
            var authoring = go.AddComponent<PlayerAuthoring>();
            authoring.MoveSpeed = 8f;
            authoring.FireInterval = 0.1f;
            return SaveAsPrefab(go, "Player");
        }

        private static GameObject CreateEnemyPrefab(Material material)
        {
            var go = CreatePrimitive(
                PrimitiveType.Cube, "Enemy", new Vector3(0.8f, 0.8f, 0.8f), material);
            var authoring = go.AddComponent<EnemyAuthoring>();
            authoring.MoveSpeed = 2.5f;
            authoring.Radius = 0.5f;
            return SaveAsPrefab(go, "Enemy");
        }

        private static GameObject CreateBulletPrefab(Material material)
        {
            var go = CreatePrimitive(
                PrimitiveType.Cube, "Bullet", new Vector3(0.25f, 0.25f, 0.25f), material);
            var authoring = go.AddComponent<BulletAuthoring>();
            authoring.MoveSpeed = 25f;
            authoring.Radius = 0.2f;
            authoring.Lifetime = 2f;
            return SaveAsPrefab(go, "Bullet");
        }

        private static void BuildScenes(
            GameObject playerPrefab, GameObject enemyPrefab, GameObject bulletPrefab)
        {
            // --- the subscene's own scene file, authored first --------------------
            var subScene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var player = (GameObject)PrefabUtility.InstantiatePrefab(playerPrefab);
            player.name = "Player";
            player.transform.position = Vector3.zero;

            var spawner = new GameObject("Spawner");
            var spawnerAuthoring = spawner.AddComponent<SpawnerAuthoring>();
            spawnerAuthoring.EnemyPrefab = enemyPrefab;
            spawnerAuthoring.BulletPrefab = bulletPrefab;
            spawnerAuthoring.InitialCount = 1000;
            spawnerAuthoring.WaveSize = 200;
            spawnerAuthoring.Interval = 0.5f;
            spawnerAuthoring.SpawnRadius = 20f;
            spawnerAuthoring.UseNaiveCollision = false;

            var arena = new GameObject("Arena");
            var arenaAuthoring = arena.AddComponent<ArenaAuthoring>();
            arenaAuthoring.Min = new Vector2(-14f, -14f);
            arenaAuthoring.Max = new Vector2(14f, 14f);

            EditorSceneManager.SaveScene(subScene, SubScenePath);

            // --- the main scene, which hosts the camera, light, and the SubScene --
            var main = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var cameraGo = new GameObject("Main Camera");
            cameraGo.tag = "MainCamera";
            var camera = cameraGo.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = 18f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.09f, 0.09f, 0.11f);
            // Straight down. The camera never moves - the arena is one screen.
            cameraGo.transform.position = new Vector3(0f, 40f, 0f);
            cameraGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            var subSceneGo = new GameObject("Workshop_SubScene");
            var subSceneComponent = subSceneGo.AddComponent<SubScene>();
            subSceneComponent.SceneAsset =
                AssetDatabase.LoadAssetAtPath<SceneAsset>(SubScenePath);
            subSceneComponent.AutoLoadScene = true;

            EditorSceneManager.SaveScene(main, ScenePath);
        }
    }
}
