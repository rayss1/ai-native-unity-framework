using AiNative.Client.Character;
using UnityEngine;

namespace AiNative.CharacterDemo
{
    [RequireComponent(typeof(CharacterHealth))]
    public sealed class TrainingTarget : MonoBehaviour
    {
        private CharacterHealth health;
        private Renderer visual;
        private MaterialPropertyBlock properties;
        private float resetAt, flashUntil, previous;
        private void Awake()
        {
            health = GetComponent<CharacterHealth>(); visual = GetComponent<Renderer>();
            properties = new MaterialPropertyBlock(); previous = health.Current;
        }
        private void Update()
        {
            if (health.Current < previous) { flashUntil = Time.time + .15f; if (health.IsDead) resetAt = Time.time + 3; }
            if (health.IsDead && Time.time >= resetAt) health.ResetHealth();
            previous = health.Current;
            properties.SetColor("_BaseColor", health.IsDead ? Color.gray : Time.time < flashUntil ? Color.white : new Color(.9f, .25f, .1f));
            visual.SetPropertyBlock(properties);
        }
        private void OnGUI()
        {
            var camera = Camera.main;
            if (camera == null || health == null) return;
            var point = camera.WorldToScreenPoint(transform.position + Vector3.up * 1.1f);
            if (point.z > 0) GUI.Box(new Rect(point.x - 75, Screen.height - point.y, 150, 25), health.IsDead ? "DOWN / reset in 3s" : "HP " + health.Current + " / " + health.Maximum);
        }
    }
}
