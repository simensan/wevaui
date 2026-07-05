using UnityEngine;
using Weva;
using Weva.Binding;
using Weva.Dom;

// Drives menu.html. Attach to the same GameObject as a WevaDocument; we wire
// ourselves in OnEnable via WevaDocument.SetController. The {{ CoinCount }} text
// node refreshes every frame the binding system observes a Version bump on the
// field; OnStart() bumps it.
public class PhaseOneDemoController : MonoBehaviour {
    [UIBind] public int CoinCount;
    [UIElement("start-button")] public Element StartButton;

    WevaDocument doc;

    void OnEnable() {
        doc = GetComponent<WevaDocument>();
        if (doc != null) doc.SetController(this);
    }

    public void OnStart() {
        Debug.Log("Weva demo: Start clicked. Coins=" + CoinCount);
        CoinCount++;
        // The binding layer reads CoinCount on the next pipeline tick; no
        // explicit MarkDirty needed for a [UIBind] field.
    }
}
