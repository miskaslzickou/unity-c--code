using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Programmatic New Input System wrapper replacing auto-generated PlayerControls / PlayerAction.
/// Contains self-contained bindings for standard keyboard, mouse, and gamepad controls.
/// </summary>
public class PlayerControls : IDisposable
{
    public InputActionAsset asset { get; }
    public PlayerActions Player { get; }
    public CameraActions Camera { get; }

    public PlayerControls()
    {
        asset = ScriptableObject.CreateInstance<InputActionAsset>();
        var playerMap = new InputActionMap("Player");
        var cameraMap = new InputActionMap("Camera");

        var throttleAction = playerMap.AddAction("Throttle", InputActionType.Value);
        throttleAction.AddCompositeBinding("1DAxis")
            .With("Positive", "<Keyboard>/w")
            .With("Negative", "<Keyboard>/s");
        throttleAction.AddCompositeBinding("1DAxis")
            .With("Positive", "<Keyboard>/upArrow")
            .With("Negative", "<Keyboard>/downArrow");
        throttleAction.AddBinding("<Gamepad>/leftStick/y");

        var turnAction = playerMap.AddAction("Turn", InputActionType.Value);
        turnAction.AddCompositeBinding("1DAxis")
            .With("Positive", "<Keyboard>/d")
            .With("Negative", "<Keyboard>/a");
        turnAction.AddCompositeBinding("1DAxis")
            .With("Positive", "<Keyboard>/rightArrow")
            .With("Negative", "<Keyboard>/leftArrow");
        turnAction.AddBinding("<Gamepad>/leftStick/x");

        var strafeAction = playerMap.AddAction("Strafe", InputActionType.Value);
        strafeAction.AddCompositeBinding("1DAxis")
            .With("Positive", "<Keyboard>/e")
            .With("Negative", "<Keyboard>/q");
        strafeAction.AddBinding("<Gamepad>/rightShoulder");

        var jumpAction = playerMap.AddAction("Jump", InputActionType.Button);
        jumpAction.AddBinding("<Keyboard>/space");
        jumpAction.AddBinding("<Gamepad>/buttonSouth");

        var fireAction = playerMap.AddAction("Fire", InputActionType.Button);
        fireAction.AddBinding("<Mouse>/leftButton");
        fireAction.AddBinding("<Gamepad>/rightTrigger");

        var lookAction = playerMap.AddAction("Look", InputActionType.PassThrough);
        lookAction.AddBinding("<Mouse>/delta");
        lookAction.AddBinding("<Gamepad>/rightStick");

        var moveAction = playerMap.AddAction("Move", InputActionType.PassThrough);
        moveAction.AddCompositeBinding("2DVector")
            .With("Up", "<Keyboard>/w")
            .With("Down", "<Keyboard>/s")
            .With("Left", "<Keyboard>/a")
            .With("Right", "<Keyboard>/d");
        moveAction.AddBinding("<Gamepad>/leftStick");

        var zoomAction = cameraMap.AddAction("Zoom", InputActionType.PassThrough);
        zoomAction.AddBinding("<Mouse>/scroll/y");
        zoomAction.AddCompositeBinding("1DAxis")
            .With("Positive", "<Keyboard>/pageUp")
            .With("Negative", "<Keyboard>/pageDown");

        var panAction = cameraMap.AddAction("Pan", InputActionType.PassThrough);
        panAction.AddBinding("<Mouse>/delta");
        panAction.AddBinding("<Gamepad>/rightStick");

        asset.AddActionMap(playerMap);
        asset.AddActionMap(cameraMap);

        Player = new PlayerActions(playerMap, throttleAction, turnAction, strafeAction, jumpAction, fireAction, lookAction, moveAction);
        Camera = new CameraActions(cameraMap, zoomAction, panAction);
    }

    public void Enable()
    {
        Player.Enable();
        Camera.Enable();
    }

    public void Disable()
    {
        Player.Disable();
        Camera.Disable();
    }

    public void Dispose()
    {
        if (asset != null)
        {
            UnityEngine.Object.DestroyImmediate(asset);
        }
    }

    public struct PlayerActions
    {
        private readonly InputActionMap map;
        public InputAction Throttle { get; }
        public InputAction Turn { get; }
        public InputAction Strafe { get; }
        public InputAction Jump { get; }
        public InputAction Fire { get; }
        public InputAction Look { get; }
        public InputAction Move { get; }

        public bool enabled => map.enabled;

        public PlayerActions(
            InputActionMap map,
            InputAction throttle,
            InputAction turn,
            InputAction strafe,
            InputAction jump,
            InputAction fire,
            InputAction look,
            InputAction move)
        {
            this.map = map;
            Throttle = throttle;
            Turn = turn;
            Strafe = strafe;
            Jump = jump;
            Fire = fire;
            Look = look;
            Move = move;
        }

        public void Enable() => map.Enable();
        public void Disable() => map.Disable();
    }

    public struct CameraActions
    {
        private readonly InputActionMap map;
        public InputAction Zoom { get; }
        public InputAction Pan { get; }

        public bool enabled => map.enabled;

        public CameraActions(InputActionMap map, InputAction zoom, InputAction pan)
        {
            this.map = map;
            Zoom = zoom;
            Pan = pan;
        }

        public void Enable() => map.Enable();
        public void Disable() => map.Disable();
    }
}

/// <summary>
/// Alias for PlayerControls to preserve backwards compatibility with legacy PlayerAction references.
/// </summary>
public class PlayerAction : PlayerControls
{
}
