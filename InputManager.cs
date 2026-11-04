using UnityEngine;

public class InputManager : MonoBehaviour
{
    public static InputManager Instance;
    public static PlayerAction Actions;

    void Awake()
    {
        Instance = this;
        Actions = new PlayerAction();
        Actions.Enable();
    }

    public static void DisablePlayerInput()
    {
        Actions.Player.Disable();
    }

    public static void EnablePlayerInput()
    {
        Actions.Player.Enable();
    }
    private void OnEnable()
    {
        Actions.Enable();
    }

    private void OnDisable()
    {
        Actions.Disable();
    }
}