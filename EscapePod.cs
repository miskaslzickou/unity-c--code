using UnityEngine;
using System.Collections;
public class EscapePod : MonoBehaviour
{

    PlayerControls playerControls;
    private Rigidbody2D rb;
    private PolygonCollider2D playerCollider;
    public float delayBeforeEscape = 2f; // Èasová prodleva pøed aktivací únikového modulu
    public float delayBeforeColision = 3f;
    public GameObject escapePodPrefab; // Prefab únikového modulu
    private Collider2D shipCollider;
    private bool triggered = false; // Zabrání opakovanému spuštìní únikového modulu
    private CameraController cameraFollow;
    private void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        playerCollider = GetComponent<PolygonCollider2D>();
        shipCollider = GetComponent<Collider2D>();
        playerControls = new PlayerControls();
        playerControls.Player.Escapepod.performed += ctx => OnEjectPerformed();
        
    }
    public void OnEjectPerformed()
    {
        // Spustí korutinu, pokud už nebyla spuštìna
        if (!triggered)
        {
            StartCoroutine(EscapePodEngage());
        }
    }
    IEnumerator EscapePodEngage()
    {
       
            triggered = true; // Nastaví, že únikový modul byl spuštìn
            yield return new WaitForSeconds(delayBeforeEscape);
            MonoBehaviour[] allScripts = GetComponents<MonoBehaviour>();
            foreach (MonoBehaviour script in allScripts)
            {
            if(script!= this)
            script.enabled = false; // Vypne všechny skripty na lodi
            }
            GameObject newPod = Instantiate(escapePodPrefab, transform.position, transform.rotation);
            Collider2D podCollider = newPod.GetComponent<Collider2D>();
            Physics2D.IgnoreCollision(shipCollider, podCollider, true);
            GameObject mainCamera = GameObject.FindWithTag("MainCamera");
            cameraFollow = mainCamera.GetComponent<CameraController>();
            cameraFollow.target = newPod.transform; // Nastaví kameru na únikový modul
            Rigidbody2D podRb = newPod.GetComponent<Rigidbody2D>();
            podRb.AddForce(transform.up * 10f, ForceMode2D.Impulse);

            yield return new WaitForSeconds(delayBeforeColision);

            Physics2D.IgnoreCollision(shipCollider, podCollider, false);
            newPod.GetComponent<Movement>().enabled = true; // Aktivuje MonoBehaviour na únikovém modulu
             // Aktivuje MonoBehaviour na únikovém modulu
        this.enabled=false; // Vypne tento skript, aby se zabránilo dalšímu spuštìní



    }
    private void OnEnable()
    {
        playerControls.Player.Enable();
    }

    private void OnDisable()
    {
        playerControls.Player.Disable();
    }
    // Update is called once per frame
    void Update()
    {
        
    }
}
