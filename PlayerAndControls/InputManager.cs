using UnityEngine;

public class InputManager : MonoBehaviour
{
    public static InputManager Instance;
    public static PlayerControls Actions;

    private void Awake()
    {
        Instance = this;
        if (Actions == null)
        {
            Actions = new PlayerControls();
        }
        Actions.Enable();
    }

    public static void DisablePlayerInput()
    {
        Actions?.Player.Disable();
    }

    public static void EnablePlayerInput()
    {
        Actions?.Player.Enable();
    }

    private void OnEnable()
    {
        Actions?.Enable();
    }

    private void OnDisable()
    {
        Actions?.Disable();
    }
}
