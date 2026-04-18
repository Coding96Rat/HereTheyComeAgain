using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// 로비 플레이어 슬롯 1개를 담당.
/// 비어있으면 "비어있음" 표시, 접속 시 플레이어 이름 표시.
/// </summary>
public class PlayerSlotUI : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI _nameText;
    [SerializeField] private Image _slotBackground;
    [SerializeField] private Color _emptyColor = new Color(0.2f, 0.2f, 0.2f, 0.8f);
    [SerializeField] private Color _occupiedColor = new Color(0.1f, 0.4f, 0.8f, 0.9f);

    public bool IsOccupied { get; private set; }

    private void Awake()
    {
        ClearSlot();
    }

    public void SetPlayer(string playerName)
    {
        IsOccupied = true;
        _nameText.text = playerName;
        if (_slotBackground != null)
            _slotBackground.color = _occupiedColor;
    }

    public void ClearSlot()
    {
        IsOccupied = false;
        _nameText.text = "비어있음";
        if (_slotBackground != null)
            _slotBackground.color = _emptyColor;
    }
}
