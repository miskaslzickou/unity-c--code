using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Interactions;

public class Movement : MonoBehaviour
{
    private PlayerControls playerControls;
    private Rigidbody2D rb;
    private PolygonCollider2D playerCollider;
    public Animator animator;
    public PlayerStats stats;


    public float throttle = 0;
    public float maxThrottle = 100;
    public float minThrottle = -20;
    public float maxSpeed = 10f;
    public float strafeSpeed = 2f;


    private float holdValue = 0f;
    private bool isHolding = false;
    public float rotationTorque = 50f;
    public float repeatRate = 0.001f; // opakovac� rychlost
    private float holdTimer = 0f;
   
    
   



    private void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        playerCollider = GetComponent<PolygonCollider2D>();

        playerControls = new PlayerControls();

     
        playerControls.Player.Throttle.performed += ctx =>
        {
            float input = ctx.ReadValue<float>();
            throttle = Mathf.Clamp(throttle + input, minThrottle, maxThrottle);
            animator.SetFloat("throttle",throttle);
            holdValue = input;
            isHolding = true;
            holdTimer = repeatRate;
        };
    
        // Uvoln�n� - zastaven� dr�en�
        playerControls.Player.Throttle.canceled += ctx =>
        {
            isHolding = false;
          
        };
        

    }

    private void OnEnable()
    {
        playerControls.Player.Enable();
    }

    private void OnDisable()
    {
        playerControls.Player.Disable();
    }
   
    private void FixedUpdate()


    {
       
      



        if (isHolding && holdValue != 0)
        {
            holdTimer -= Time.fixedDeltaTime;
            if (holdTimer <= 0f)
            {
                throttle = Mathf.Clamp(throttle + holdValue, minThrottle, maxThrottle);
                animator.SetFloat("throttle", throttle);
               
                holdTimer = repeatRate;
            }
        }
        float rotateInput = playerControls.Player.Turn.ReadValue<float>();
        float strafeInput = playerControls.Player.Strafe.ReadValue<float>();
        stats.throttle = throttle;
        stats.speed = Mathf.Round(rb.linearVelocity.magnitude);
        stats.heading = Mathf.Round(transform.eulerAngles.z);
        rb.AddForce(transform.up * throttle * maxSpeed);
        rb.AddForce(-transform.right*strafeInput*strafeSpeed);
        rb.AddTorque(rotateInput * rotationTorque);
        

    }
}
