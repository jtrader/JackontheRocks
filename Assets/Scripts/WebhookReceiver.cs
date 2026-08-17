using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// Simple local HTTP listener that accepts webhook POSTs and forwards token+payload to SaveManager.
/// Posts should be JSON: { "token": "...", "payload": {...} }
/// </summary>
public class WebhookReceiver : MonoBehaviour
{
    public int port = 8080;
    public string path = "/webhook";
        [Tooltip("Optional shared secret. If set, incoming requests must include header X-Webhook-Secret with this value.")]
        public string requiredSecret = "";

    private HttpListener listener;
    private Thread listenerThread;
    private readonly Queue<Action> callbacks = new Queue<Action>();
    private readonly object lockObj = new object();

    [Serializable]
    private class WebhookPayload
    {
        public string token;
        public string payload;
    }

    void Start()
    {
        try
        {
            listener = new HttpListener();
            string prefix = $"http://*:{port}{path.TrimEnd('/')}/";
            listener.Prefixes.Add(prefix);
            listener.Start();
            listenerThread = new Thread(ListenLoop) { IsBackground = true };
            listenerThread.Start();
            Debug.Log($"WebhookReceiver listening on {prefix}");
        }
        catch (Exception e)
        {
            Debug.LogWarning("Failed to start WebhookReceiver: " + e.Message);
        }
    }

    void OnDestroy()
    {
        try
        {
            listener?.Close();
            if (listenerThread != null && listenerThread.IsAlive) listenerThread.Abort();
        }
        catch { }
    }

    private void ListenLoop()
    {
        while (listener != null && listener.IsListening)
        {
            try
            {
                var ctx = listener.GetContext();
                ThreadPool.QueueUserWorkItem(_ => HandleContext(ctx));
            }
            catch (HttpListenerException) { break; }
            catch (Exception) { break; }
        }
    }

    private void HandleContext(HttpListenerContext ctx)
    {
        try
        {
            // Check shared secret header if configured
            if (!string.IsNullOrEmpty(requiredSecret))
            {
                var headerSecret = ctx.Request.Headers["X-Webhook-Secret"];
                if (string.IsNullOrEmpty(headerSecret) || headerSecret != requiredSecret)
                {
                    ctx.Response.StatusCode = 401;
                    var err = Encoding.UTF8.GetBytes("Unauthorized");
                    try { ctx.Response.OutputStream.Write(err, 0, err.Length); } catch { }
                    ctx.Response.Close();
                    return;
                }
            }

            if (ctx.Request.HttpMethod != "POST")
            {
                ctx.Response.StatusCode = 405;
                ctx.Response.Close();
                return;
            }

            string body;
            using (var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding)) body = sr.ReadToEnd();

            // Try parse JSON
            string token = null;
            string payloadJson = null;
            try
            {
                var wp = JsonUtility.FromJson<WebhookPayload>(body);
                if (wp != null)
                {
                    token = wp.token;
                    // payload may be an object or string; try to preserve original
                    payloadJson = wp.payload;
                }
            }
            catch { }

            // Enqueue action to run on main thread
            lock (lockObj)
            {
                callbacks.Enqueue(() => { OnWebhookReceived(token, payloadJson); });
            }

            var respBytes = Encoding.UTF8.GetBytes("OK");
            ctx.Response.StatusCode = 200;
            ctx.Response.OutputStream.Write(respBytes, 0, respBytes.Length);
            ctx.Response.Close();
        }
        catch (Exception e)
        {
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
            Debug.LogWarning("WebhookReceiver error: " + e.Message);
        }
    }

    void Update()
    {
        // Process queued callbacks
        Action action = null;
        lock (lockObj)
        {
            if (callbacks.Count > 0) action = callbacks.Dequeue();
        }
        if (action != null) action();
    }

    private void OnWebhookReceived(string token, string payloadJson)
    {
        Debug.Log("Webhook received token=" + (token != null ? "(present)" : "(null)"));
        var saver = FindObjectOfType<JackOnTheRocks.SaveManager>();
        if (saver != null)
        {
            saver.ApplyServerReceipt(token, payloadJson);
        }
        else
        {
            Debug.LogWarning("SaveManager not found in scene; webhook ignored");
        }
    }
}
