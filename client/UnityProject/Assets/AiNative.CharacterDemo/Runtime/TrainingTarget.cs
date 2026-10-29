using AiNative.Client.Character;
using UnityEngine;

namespace AiNative.CharacterDemo
{
    [RequireComponent(typeof(CharacterHealth))]
    public sealed class TrainingTarget : MonoBehaviour
    {
        private CharacterHealth _health;
        private Renderer _visual;
        private MaterialPropertyBlock _properties;
        private float _resetAt;
        private float _flashUntil;
        private float _previous;

        private void Awake()
        {
            _health = GetComponent<CharacterHealth>();
            _visual = GetComponent<Renderer>();
            _properties = new MaterialPropertyBlock();
            _previous = _health.Current;
        }

        private void Update()
        {
            if (_health.Current < _previous)
            {
                _flashUntil = Time.time + .15f;
                if (_health.IsDead)
                {
                    _resetAt = Time.time + 3;
                }
            }

            if (_health.IsDead && Time.time >= _resetAt)
            {
                _health.ResetHealth();
            }

            _previous = _health.Current;
            Color targetColor;
            if (_health.IsDead)
            {
                targetColor = Color.gray;
            }
            else
            {
                targetColor = Time.time < _flashUntil ? Color.white : new Color(.9f, .25f, .1f);
            }
            _properties.SetColor("_BaseColor", targetColor);
            _visual.SetPropertyBlock(_properties);
        }

        private void OnGUI()
        {
            var camera = Camera.main;
            if (camera == null || _health == null)
            {
                return;
            }

            var point = camera.WorldToScreenPoint(transform.position + Vector3.up * 1.1f);
            if (point.z > 0)
            {
                GUI.Box(
                    new Rect(point.x - 75, Screen.height - point.y, 150, 25),
                    _health.IsDead
                        ? "DOWN / reset in 3s"
                        : "HP " + _health.Current + " / " + _health.Maximum
                );
            }
        }
    }
}
