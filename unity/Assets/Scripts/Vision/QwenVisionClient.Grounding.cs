using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Yixuan.Vision
{
    public sealed class VisionGroundingReply
    {
        public string Content = "", Raw = "", Error = "", Model = "";
        public int HttpStatus;
        public float Seconds;
        public bool FromBackoff;
        public VisionServiceDiagnostic Diagnostic;
    }

    public partial class QwenVisionClient
    {
        public IEnumerator AskGrounded(string prompt, byte[] jpeg, byte[] annotatedJpeg, Func<bool> cancelled,
            Action<VisionGroundingReply> completed)
        {
            var reply = new VisionGroundingReply { Model = string.IsNullOrEmpty(Model) ? DefaultModel : Model };
            float started = Time.realtimeSinceStartup;
            if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 12000 || jpeg == null || jpeg.Length == 0)
                reply.Error = "invalid_grounding_input";
            else if (ServiceBlocked)
            { reply.Error = ServiceStatus; reply.FromBackoff = true; reply.Diagnostic = _blockedDiagnostic; }
            else if (!HasKey && !LoadKey()) reply.Error = "未找到视觉服务配置";
            if (!string.IsNullOrEmpty(reply.Error))
            {
                if (completed != null) completed(reply);
                yield break;
            }
            using (var cancellation = new CancellationTokenSource())
            {
                Task<string> pending = null;
                try
                {
                    pending = PostJsonAsync(BuildJson(jpeg, annotatedJpeg, prompt, 700), cancellation.Token,
                        (model, status) => { reply.Model = model; reply.HttpStatus = status; }, 1, false);
                    while (!pending.IsCompleted)
                    {
                        if (cancelled != null && cancelled()) { reply.Error = "cancelled"; break; }
                        if (Time.realtimeSinceStartup - started > Mathf.Max(5f, TimeoutSec))
                        { reply.Error = "视觉定位超时"; break; }
                        yield return null;
                    }
                    if (string.IsNullOrEmpty(reply.Error))
                    {
                        if (pending.IsCanceled) reply.Error = "cancelled";
                        else if (pending.IsFaulted)
                        {
                            var failure = pending.Exception.GetBaseException();
                            reply.Error = VisionServiceDiagnostic.Redact(failure.Message, _apiKey, 600);
                            var serviceFailure = failure as VisionServiceException;
                            if (serviceFailure != null)
                            { reply.Diagnostic = serviceFailure.Diagnostic; reply.FromBackoff = serviceFailure.FromBackoff; }
                        }
                        else
                        {
                            reply.Raw = pending.Result;
                            reply.Content = ParseContent(reply.Raw);
                            if (string.IsNullOrWhiteSpace(reply.Content)) reply.Error = "视觉模型返回空内容";
                        }
                    }
                }
                finally
                {
                    if (pending != null && !pending.IsCompleted) cancellation.Cancel();
                }
            }
            reply.Seconds = Time.realtimeSinceStartup - started;
            if (completed != null) completed(reply);
        }
    }
}
