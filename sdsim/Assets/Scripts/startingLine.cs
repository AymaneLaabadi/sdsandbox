using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class startingLine : MonoBehaviour
{
    public int index = 0;
    string target = "body";
    PrivateAPI privateAPI;

    void Awake()
    {
        // Segmentation: the start line lies on the road and is drivable -> class 1 (road).
        if (GetComponent<Renderer>() != null && GetComponent<SegLabel>() == null)
        {
            SegLabel label = gameObject.AddComponent<SegLabel>();
            label.classId = SegLabel.CLASS_ROAD;
            label.Apply();
        }
    }

    void Start()
    {
        privateAPI = GameObject.FindObjectOfType<PrivateAPI>();
    }

    void OnTriggerEnter(Collider col)
    {
        if (col.gameObject.name != target) { return; }
        float time = Time.fixedTime;

        Transform parent = col.transform.parent.parent;
        if (parent == null) { return; }

        string carName = parent.name;
        tk.TcpCarHandler client = parent.GetComponentInChildren<tk.TcpCarHandler>();

        if (client != null)
            UnityMainThreadDispatcher.Instance().Enqueue(client.SendCollisionWithStartingLine(index, time));

        if (privateAPI != null)
            privateAPI.CollisionWithStatingLine(carName, index, time);
    }
}
