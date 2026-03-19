using TMPro;
using UnityEngine;

public class FPSDISPLAY : MonoBehaviour
{
    public TextMeshProUGUI fpsText;
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        fpsText.text = "FPS: " + (1.0f / Time.deltaTime).ToString("F2");
    }
}
