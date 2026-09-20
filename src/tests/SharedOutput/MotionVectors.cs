using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Numerics;
using static UnityRemix.RemixAPI;

unsafe partial class Program
{
    static void ProbeMotion(IntPtr device, IntPtr context, IntPtr texture, Desc desc,
        PFN_remixapi_SetupCamera setup, PFN_remixapi_DrawInstance draw, PFN_remixapi_Present present,
        RenderEvent render, State state, ref remixapi_CameraInfo camera,
        ref remixapi_CameraInfoParameterizedEXT parameters, ref remixapi_InstanceInfo instance,
        ref remixapi_PresentInfo frame, uint renderWidth, uint renderHeight)
    {
        // Drain the warm-up queue before correlating one camera update with one copy.
        for (int i = 0; i < 40; i++) { render(0); Thread.Sleep(5); }
        foreach (var step in new (string Name, float X, float Y, float ObjectX, float ObjectY, float Dx, float Dy)[] {
            ("settle", 0, 0, 0, 0, 0, 0), ("stationary", 0, 0, 0, 0, 0, 0),
            ("camera right", .2f, 0, 0, 0, .2f, 0), ("camera stopped", .2f, 0, 0, 0, 0, 0),
            ("camera left", 0, 0, 0, 0, -.2f, 0), ("camera up", 0, .2f, 0, 0, 0, .2f),
            ("camera down", 0, 0, 0, 0, 0, -.2f), ("stopped again", 0, 0, 0, 0, 0, 0),
            ("object right", 0, 0, .2f, 0, -.2f, 0), ("object stopped", 0, 0, .2f, 0, 0, 0),
            ("object left", 0, 0, 0, 0, .2f, 0), ("object up", 0, 0, 0, .2f, 0, -.2f),
            ("object down", 0, 0, 0, 0, 0, .2f), ("object stopped again", 0, 0, 0, 0, 0, 0) })
        {
            parameters.position = new remixapi_Float3D(step.X, step.Y, 0);
            instance.transform = remixapi_Transform.FromMatrix(1, 0, 0, step.ObjectX, 0, 1, 0, step.ObjectY, 0, 0, 1, 0);
            ulong before = state() >> 8;
            Check(setup(ref camera) == 0 && draw(ref instance) == 0 && present(ref frame) == 0,
                step.Name + " submitted");
            // Unity submits one copy before drawing its camera. Polling until a
            // later frame is ready hides the stale-image bug seen during motion.
            render(0);
            Check((state() >> 8) == before + 1, step.Name + " current frame copied without polling");
            byte[] pixels = ReadPixels(device, context, texture, desc);
            double x = 0, y = 0, blue = 0;
            int count = 0;
            for (int py = (int)desc.Height / 2 - 8; py < desc.Height / 2 + 8; py++)
                for (int px = (int)desc.Width / 2 - 8; px < desc.Width / 2 + 8; px++)
                {
                    int offset = (py * (int)desc.Width + px) * 4;
                    x += (pixels[offset] / 255.0 - .5) * renderWidth / 8;
                    y += (pixels[offset + 1] / 255.0 - .5) * renderHeight / 8;
                    blue += pixels[offset + 2];
                    count++;
                }
            x /= count; y /= count; blue /= count;
            double focalY = 1.0 / Math.Tan(parameters.fovYInDegrees * Math.PI / 360);
            double expectedX = step.Dx * renderWidth * focalY / (2 * parameters.aspect * 3);
            double expectedY = -step.Dy * renderHeight * focalY / (2 * 3);
            string result = $"MOTION {step.Name}: measured=({x:F3}, {y:F3}) display px; expected=({expectedX:F3}, {expectedY:F3}) px; blue={blue:F1}";
            Console.WriteLine(result);
            File.AppendAllText(Log, result + Environment.NewLine);
            // Normalize by the actual G-buffer extent in the shader, then express
            // signed displacement in display pixels independently of DLSS scaling.
            Check(blue < 1 && Math.Abs(x - expectedX) < .6 && Math.Abs(y - expectedY) < .6,
                step.Name + " motion-vector sign, axes, magnitude and history");
        }
        // Exercise rotation as well as translation, including the reflected
        // Y/Z basis used by UnityRemix's real camera and geometry conversion.
        foreach (bool unityBasis in new[] { false, true })
        {
            Vector3 previousRight = Vector3.UnitX, previousUp = Vector3.UnitY, previousForward = Vector3.UnitZ;
            instance.transform = unityBasis
                ? remixapi_Transform.FromMatrix(1, 0, 0, 0, 0, 0, 1, 0, 0, 1, 0, 0)
                : remixapi_Transform.FromMatrix(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0);
            parameters.position = new remixapi_Float3D(0, 0, 0);
            foreach (var rotation in new (string Name, float Yaw, float Pitch, float Roll)[] {
                ("rotation settle", 0, 0, 0), ("rotation stationary", 0, 0, 0),
                ("yaw right", .04f, 0, 0), ("yaw stopped", .04f, 0, 0), ("yaw left", 0, 0, 0),
                ("pitch", 0, .04f, 0), ("pitch stopped", 0, .04f, 0), ("pitch return", 0, 0, 0),
                ("roll", 0, 0, .06f), ("roll stopped", 0, 0, .06f), ("roll return", 0, 0, 0) })
            {
                var orientation = Quaternion.CreateFromYawPitchRoll(rotation.Yaw, rotation.Pitch, rotation.Roll);
                var right = Vector3.Transform(Vector3.UnitX, orientation);
                var up = Vector3.Transform(Vector3.UnitY, orientation);
                var forward = Vector3.Transform(Vector3.UnitZ, orientation);
                parameters.right = Basis(right, unityBasis);
                parameters.up = Basis(up, unityBasis);
                parameters.forward = Basis(forward, unityBasis);
                ulong before = state() >> 8;
                Check(setup(ref camera) == 0 && draw(ref instance) == 0 && present(ref frame) == 0, rotation.Name + " submitted");
                render(0);
                Check((state() >> 8) == before + 1, rotation.Name + " current frame copied without polling");
                byte[] pixels = ReadPixels(device, context, texture, desc);
                double measuredX = 0, measuredY = 0, expectedX = 0, expectedY = 0;
                double focalY = 1 / Math.Tan(parameters.fovYInDegrees * Math.PI / 360);
                double focalX = focalY / parameters.aspect;
                int count = 0;
                // An off-center patch makes roll observable instead of cancelling
                // positive and negative motion around the optical center.
                for (int py = (int)desc.Height / 2 + 24; py < desc.Height / 2 + 40; py++)
                    for (int px = (int)desc.Width / 2 + 24; px < desc.Width / 2 + 40; px++)
                    {
                        double ndcX = 2 * (px + .5) / desc.Width - 1;
                        double ndcY = 1 - 2 * (py + .5) / desc.Height;
                        Vector3 ray = forward + right * (float)(ndcX / focalX) + up * (float)(ndcY / focalY);
                        double depth = Vector3.Dot(ray, previousForward);
                        expectedX += (Vector3.Dot(ray, previousRight) * focalX / depth - ndcX) * renderWidth / 2;
                        expectedY -= (Vector3.Dot(ray, previousUp) * focalY / depth - ndcY) * renderHeight / 2;
                        int offset = (py * (int)desc.Width + px) * 4;
                        measuredX += (pixels[offset] / 255.0 - .5) * renderWidth / 8;
                        measuredY += (pixels[offset + 1] / 255.0 - .5) * renderHeight / 8;
                        count++;
                    }
                measuredX /= count; measuredY /= count; expectedX /= count; expectedY /= count;
                string result = $"CAMERA {(unityBasis ? "Unity Y/Z" : "native")} {rotation.Name}: measured=({measuredX:F3}, {measuredY:F3}) expected=({expectedX:F3}, {expectedY:F3}) display px";
                Console.WriteLine(result);
                File.AppendAllText(Log, result + Environment.NewLine);
                if (rotation.Name != "rotation settle")
                    Check(Math.Abs(measuredX - expectedX) < .6 && Math.Abs(measuredY - expectedY) < .6, rotation.Name + " projected camera rotation/history");
                previousRight = right; previousUp = up; previousForward = forward;
            }
        }
    }
    static remixapi_Float3D Basis(Vector3 value, bool unityBasis) => unityBasis
        ? new remixapi_Float3D(value.X, value.Z, value.Y)
        : new remixapi_Float3D(value.X, value.Y, value.Z);
}
