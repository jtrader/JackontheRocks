Example scene generator for JackOnTheRocks.

Usage:
1. Open Unity Editor for this project.
2. From the top menu choose `JackOnTheRocks -> Create Example Scene`.
3. The script will create a new scene containing:
   - `JackOnTheRocksManager` GameObject
   - `DemoUI` GameObject with `JackOnTheRocksDemoUI` attached
   - `Canvas` with several UI buttons named: `StartRound`, `Hit`, `Stand`, `DoubleDown`, `BuyDrink`, `TipWaiter_Tip`, `TipWaiter_RequestDance`, `TipWaiter_Strip`, `GrantDiamonds`
   - `SceneSetup` GameObject that wires those buttons to the demo methods at runtime
4. Press Play to test interactions and watch the console for debug output.

Notes:
- The scene generator uses editor APIs and will not run at runtime. Use the menu in the Editor.
- Buttons are wired at runtime by `SceneSetup` so no persistent UnityEvent serialized wiring is required.
