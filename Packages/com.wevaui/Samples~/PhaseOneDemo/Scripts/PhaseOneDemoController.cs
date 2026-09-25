using UnityEngine;
using Weva;
using Weva.Binding;

// Drives menu.html. Attach to the same GameObject as a WevaDocument; we wire
// ourselves in OnEnable via WevaDocument.SetController. The {{ CoinCount }}
// text node follows the field: a controller without IBindingVersion is read
// once a frame, so mutating the field is enough.
public class PhaseOneDemoController : MonoBehaviour {
    [UIBind] public int CoinCount;

    WevaDocument doc;

    void OnEnable() {
        doc = GetComponent<WevaDocument>();
        if (doc != null) doc.SetController(this);
    }

    public void OnStart() {
        Debug.Log("Weva demo: Start clicked. Coins=" + CoinCount);
        CoinCount++;
    }
}
