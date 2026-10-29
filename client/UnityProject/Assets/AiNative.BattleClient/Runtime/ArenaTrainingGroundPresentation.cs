using System;
using AiNative.Gameplay;
using UnityEngine;

namespace AiNative.Client.Application
{
    /// <summary>由应用持有的训练场表现；地图规则来自以毫米为单位的 Shared 数据。</summary>
    [DisallowMultipleComponent]
    public sealed class ArenaTrainingGroundPresentation : MonoBehaviour
    {
        private readonly Material[] _materials = new Material[6];

        private void Awake()
        {
            Material source = Resources.Load<Material>("BattleClient/Floor");
            if (source == null)
            {
                throw new InvalidOperationException("Missing BattleClient/Floor material.");
            }

            _materials[0] = CreateMaterial(source, "Deck", new Color(.075f, .105f, .145f));
            _materials[1] = CreateMaterial(source, "Armor", new Color(.22f, .29f, .36f));
            _materials[2] = CreateMaterial(source, "Trim", new Color(.07f, .12f, .18f));
            _materials[3] = CreateMaterial(source, "Cyan", new Color(.05f, .65f, .95f), true);
            _materials[4] = CreateMaterial(source, "Amber", new Color(1f, .38f, .06f), true);
            _materials[5] = CreateMaterial(source, "Markings", new Color(.45f, .62f, .72f));
            float extent = ArenaTrainingMap.HalfExtent / 1000f;
            Box(
                "TrainingDeck",
                new Vector3(0, -.15f, 0),
                new Vector3(extent * 2, .3f, extent * 2),
                0,
                true
            );
            for (int i = 0; i < ArenaTrainingMap.ObstacleCount; i++)
            {
                ArenaMapBox bounds = ArenaTrainingMap.GetObstacle(i);
                Vector3 center =
                    new Vector3(
                        bounds.MinX + bounds.MaxX,
                        bounds.MinY + bounds.MaxY,
                        bounds.MinZ + bounds.MaxZ
                    ) / 2000f;
                Vector3 size =
                    new Vector3(
                        bounds.MaxX - bounds.MinX,
                        bounds.MaxY - bounds.MinY,
                        bounds.MaxZ - bounds.MinZ
                    ) / 1000f;
                Box("SharedObstacle_" + i, center, size, 1, true);
                // 着色顶边用于突出轮廓，不增加碰撞几何。
                int accent = center.x < 0 ? 3 : 4;
                Box(
                    "ObstacleLight_" + i,
                    new Vector3(center.x, bounds.MaxY / 1000f + .006f, center.z),
                    new Vector3(size.x, .012f, Mathf.Min(.08f, size.z)),
                    accent,
                    false
                );
                Box(
                    "ObstacleCap_" + i,
                    new Vector3(center.x, bounds.MaxY / 1000f + .004f, center.z),
                    new Vector3(size.x * .9f, .008f, size.z * .8f),
                    2,
                    false
                );
            }

            // 贴地标线不带碰撞体；所有可交互掩体均来自 Shared。
            for (int grid = -16; grid <= 16; grid += 4)
            {
                Box(
                    "DeckGridX",
                    new Vector3(grid, .002f, 0),
                    new Vector3(.025f, .004f, 39),
                    2,
                    false
                );
                Box(
                    "DeckGridZ",
                    new Vector3(0, .002f, grid),
                    new Vector3(39, .004f, .025f),
                    2,
                    false
                );
            }

            for (int side = -1; side <= 1; side += 2)
            {
                int accent = side < 0 ? 3 : 4;
                Box(
                    "PerimeterGuide",
                    new Vector3(side * 19.4f, .009f, 0),
                    new Vector3(.08f, .018f, 38.8f),
                    accent,
                    false
                );
                Box(
                    "PerimeterGuide",
                    new Vector3(0, .009f, side * 19.4f),
                    new Vector3(38.8f, .018f, .08f),
                    5,
                    false
                );
                for (int lane = -1; lane <= 1; lane += 2)
                {
                    Vector3 spawn = new Vector3(side * 16, .012f, lane * 12);
                    Outline("SpawnPad", spawn, 3, 3, accent);
                    for (int stripe = 0; stripe < 3; stripe++)
                    {
                        Box(
                            "SpawnDirection",
                            spawn + new Vector3(-side * (1.9f + stripe * .4f), 0, 0),
                            new Vector3(.12f, .012f, 1),
                            accent,
                            false
                        );
                    }
                }
            }

            Outline("CenterCourt", new Vector3(0, .008f, 0), 16, 16, 5);
        }

        private Material CreateMaterial(
            Material source,
            string label,
            Color color,
            bool emissive = false
        )
        {
            var material = new Material(source) { name = "TrainingGround_" + label };
            material.color = color;
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }

            if (material.HasProperty("_Smoothness"))
            {
                material.SetFloat("_Smoothness", .3f);
            }

            if (emissive && material.HasProperty("_EmissionColor"))
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", color * 1.5f);
            }

            return material;
        }

        private void Outline(string label, Vector3 center, float width, float depth, int material)
        {
            Box(
                label,
                center + Vector3.left * width * .5f,
                new Vector3(.08f, .012f, depth),
                material,
                false
            );
            Box(
                label,
                center + Vector3.right * width * .5f,
                new Vector3(.08f, .012f, depth),
                material,
                false
            );
            Box(
                label,
                center + Vector3.forward * depth * .5f,
                new Vector3(width, .012f, .08f),
                material,
                false
            );
            Box(
                label,
                center + Vector3.back * depth * .5f,
                new Vector3(width, .012f, .08f),
                material,
                false
            );
        }

        private void Box(string label, Vector3 position, Vector3 size, int material, bool solid)
        {
            GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = label;
            box.transform.SetParent(transform, false);
            box.transform.localPosition = position;
            box.transform.localScale = size;
            box.GetComponent<Renderer>().sharedMaterial = _materials[material];
            if (!solid)
            {
                Collider collider = box.GetComponent<Collider>();
                collider.enabled = false;
                Destroy(collider);
            }
        }

        private void OnDestroy()
        {
            for (int i = 0; i < _materials.Length; i++)
            {
                if (_materials[i] != null)
                {
                    Destroy(_materials[i]);
                }
            }
        }
    }
}
