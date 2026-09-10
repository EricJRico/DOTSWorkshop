using UnityEngine;
using UnityEngine.UI;

namespace Workshop
{
    /// <summary>
    /// Provided. The HUD's own controller: it listens to <see cref="PlayerHealth"/> and is the
    /// only thing that touches the bar. Nothing pushes values into it.
    /// </summary>
    public class HealthBarView : MonoBehaviour
    {
        [SerializeField] PlayerHealth _health;

        [Tooltip("The bar's fill. Its width comes from the right anchor rather than Image's " +
                 "Filled type: Filled clips the sprite's UVs, so a sprite with rounded corners " +
                 "gets cut and squashed as the bar empties.")]
        [SerializeField] Image _fill;

        [Header("Colours")]
        [SerializeField] Color _empty = new Color(0.85f, 0.15f, 0.15f);
        [SerializeField] Color _full = new Color(0.30f, 0.80f, 0.35f);

        void OnEnable()
        {
            if (_health == null) return;
            _health.Changed += Redraw;
            Redraw(_health.Normalised);
        }

        void OnDisable()
        {
            if (_health != null) _health.Changed -= Redraw;
        }

        void Redraw(float normalised)
        {
            if (_fill == null) return;

            var rect = _fill.rectTransform;
            rect.anchorMin = new Vector2(0f, rect.anchorMin.y);
            rect.anchorMax = new Vector2(normalised, rect.anchorMax.y);
            _fill.color = Color.Lerp(_empty, _full, normalised);
        }
    }
}
