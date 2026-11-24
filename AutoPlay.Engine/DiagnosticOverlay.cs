using System.Drawing;
using System.Windows.Forms;

namespace AutoPlay.Engine;

internal sealed class DiagnosticOverlay : IDisposable
{
    private readonly Thread thread;
    private readonly OverlayForm form;

    public DiagnosticOverlay(GameWindow window)
    {
        var ready = new TaskCompletionSource<OverlayForm>(TaskCreationOptions.RunContinuationsAsynchronously);
        thread = new Thread(() =>
        {
            try
            {
                var overlay = new OverlayForm(window);
                overlay.HandleCreated += (_, _) => ready.TrySetResult(overlay);
                Application.Run(overlay);
            }
            catch (Exception ex) { ready.TrySetException(ex); }
        }) { IsBackground = true, Name = "Recognition overlay" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        form = ready.Task.GetAwaiter().GetResult();
    }

    public void Publish(IReadOnlyList<Mark> marks, Size frameSize) => form.BeginInvoke(() => form.UpdateMarks(marks, frameSize));

    public void Dispose()
    {
        if (!form.IsDisposed) form.BeginInvoke(form.Close);
        thread.Join();
    }

    private sealed class OverlayForm : Form
    {
        private readonly GameWindow window;
        private readonly System.Windows.Forms.Timer timer;
        private IReadOnlyList<Mark> marks = [];
        private Size frameSize;

        public OverlayForm(GameWindow window)
        {
            this.window = window;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            DoubleBuffered = true;
            timer = new System.Windows.Forms.Timer { Interval = 200 };
            timer.Tick += (_, _) => Align();
            timer.Start();
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x20 | 0x80 | 0x80000 | 0x08000000;
                return cp;
            }
        }

        public void UpdateMarks(IReadOnlyList<Mark> value, Size size)
        {
            marks = value;
            frameSize = size;
            Align();
            Invalidate();
        }

        private void Align()
        {
            if (!Native.IsWindow(window.Handle) || !Native.IsWindowVisible(window.Handle) || Native.IsIconic(window.Handle))
            {
                Hide();
                return;
            }
            Bounds = window.ClientBounds();
            if (!Visible) Show();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (frameSize.Width == 0 || frameSize.Height == 0) return;
            e.Graphics.ScaleTransform(ClientSize.Width / (float)frameSize.Width, ClientSize.Height / (float)frameSize.Height);
            RecognitionFrame.DrawMarks(e.Graphics, marks);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
