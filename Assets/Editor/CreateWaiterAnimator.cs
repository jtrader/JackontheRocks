using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Editor utility to create a sample Animator Controller for waiter visuals.
/// Menu: JackOnTheRocks/Create Waiter Animator Controller
/// </summary>
public static class CreateWaiterAnimator
{
    [MenuItem("JackOnTheRocks/Create Waiter Animator Controller")]
    public static void CreateController()
    {
        string path = "Assets/Animations/WaiterController.controller";
        // Ensure folder exists
        System.IO.Directory.CreateDirectory("Assets/Animations");

        // Create controller
        var controller = AnimatorController.CreateAnimatorControllerAtPath(path);

        // Add parameters: ClothingTier (int), isDancing (bool), ChangeClothing (trigger)
        controller.AddParameter("ClothingTier", AnimatorControllerParameterType.Int);
        controller.AddParameter("isDancing", AnimatorControllerParameterType.Bool);
        controller.AddParameter("ChangeClothing", AnimatorControllerParameterType.Trigger);

        // Setup states on default layer
        var layer = controller.layers[0];
        var stateMachine = layer.stateMachine;

        // Create Idle state
        var idle = stateMachine.AddState("Idle");

        // Create ChangeClothing state (could be a short transition)
        var change = stateMachine.AddState("ChangeClothing");

        // Create Dance state
        var dance = stateMachine.AddState("Dance");

        // Transitions: Idle -> ChangeClothing on trigger ChangeClothing
        var t1 = idle.AddTransition(change);
        t1.hasExitTime = false;
        t1.AddCondition(AnimatorConditionMode.If, 0, "ChangeClothing");

        // ChangeClothing -> Idle after exit time
        var t2 = change.AddTransition(idle);
        t2.hasExitTime = true;
        t2.exitTime = 1.0f;

        // Idle <-> Dance driven by isDancing bool
        var t3 = idle.AddTransition(dance);
        t3.hasExitTime = false;
        t3.AddCondition(AnimatorConditionMode.If, 0, "isDancing");

        var t4 = dance.AddTransition(idle);
        t4.hasExitTime = false;
        t4.AddCondition(AnimatorConditionMode.IfNot, 0, "isDancing");

        // Create simple AnimationClips for Idle and Dance
        string idleClipPath = "Assets/Animations/Idle.anim";
        string danceClipPath = "Assets/Animations/Dance.anim";

        var idleClip = new AnimationClip();
        idleClip.name = "Idle";
        idleClip.legacy = false;
        // Idle: subtle breathing scale animation
        var idleCurve = new AnimationCurve(new Keyframe(0, 1f), new Keyframe(0.5f, 1.02f), new Keyframe(1f, 1f));
        AnimationUtility.SetEditorCurve(idleClip, EditorCurveBinding.FloatCurve("", typeof(Transform), "m_LocalScale.x"), idleCurve);
        AnimationUtility.SetEditorCurve(idleClip, EditorCurveBinding.FloatCurve("", typeof(Transform), "m_LocalScale.y"), idleCurve);
        AnimationUtility.SetEditorCurve(idleClip, EditorCurveBinding.FloatCurve("", typeof(Transform), "m_LocalScale.z"), idleCurve);

        var danceClip = new AnimationClip();
        danceClip.name = "Dance";
        danceClip.legacy = false;
        // Dance: pulsing and small rotation
        var danceScaleCurve = new AnimationCurve(new Keyframe(0, 1f), new Keyframe(0.25f, 1.05f), new Keyframe(0.5f, 1f), new Keyframe(0.75f, 1.05f), new Keyframe(1f, 1f));
        var danceRotCurve = new AnimationCurve(new Keyframe(0, 0f), new Keyframe(0.5f, 5f), new Keyframe(1f, 0f));
        AnimationUtility.SetEditorCurve(danceClip, EditorCurveBinding.FloatCurve("", typeof(Transform), "m_LocalScale.x"), danceScaleCurve);
        AnimationUtility.SetEditorCurve(danceClip, EditorCurveBinding.FloatCurve("", typeof(Transform), "m_LocalScale.y"), danceScaleCurve);
        AnimationUtility.SetEditorCurve(danceClip, EditorCurveBinding.FloatCurve("", typeof(Transform), "m_LocalScale.z"), danceScaleCurve);
        AnimationUtility.SetEditorCurve(danceClip, EditorCurveBinding.FloatCurve("", typeof(Transform), "localEulerAnglesRaw.z"), danceRotCurve);

        AssetDatabase.CreateAsset(idleClip, idleClipPath);
        AssetDatabase.CreateAsset(danceClip, danceClipPath);
        AssetDatabase.SaveAssets();

        // Assign clips to states
        idle.motion = idleClip;
        dance.motion = danceClip;

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("Created Animator Controller with clips at " + path);
    }
}
