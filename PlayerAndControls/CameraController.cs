using UnityEngine;

public class CameraController : MonoBehaviour
{
  
    public Transform target; 
    public Vector3 offset = new Vector3(0f, 0f, -10f); 
    public float cameraMoveSpeed = 10f;
    private PlayerControls playerControls;
    private void Awake()
    {
        playerControls = new PlayerControls();
        playerControls.Camera.Zoom.performed += ctx =>
        {
            float zoomInput = ctx.ReadValue<float>();
            Camera.main.orthographicSize = Mathf.Clamp(Camera.main.orthographicSize - zoomInput, 0.9f, 4.9f);
        };
    }
    private void OnEnable()
    {
        playerControls.Camera.Enable();
    }

    private void OnDisable()
    {
        playerControls.Camera.Disable();
    }
    void LateUpdate()
    {
        if (target == null)
        {
            Debug.LogWarning("CameraController: Target player not assigned!");
            return;
        }


        Vector3 desiredPosition = target.position + offset;

        
        transform.position= Vector3.MoveTowards(transform.position,desiredPosition,cameraMoveSpeed*Time.deltaTime);

      

    }
}
