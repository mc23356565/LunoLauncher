using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace OpenLauncher.Core
{
    public enum TaskState { Running, Done, Failed, Canceled }

    /// <summary>一个后台任务（安装版本、下载 Mod、导入整合包……），界面上的任务列表直接绑定它</summary>
    public class LauncherTask : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        void On([CallerMemberName] string p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        public CancellationTokenSource Cts { get; } = new CancellationTokenSource();
        public CancellationToken Token => Cts.Token;
        public DateTime Started { get; } = DateTime.Now;

        string _title, _detail, _error;
        double _progress;
        long _speed;
        TaskState _state = TaskState.Running;

        public string Title { get => _title; set { _title = value; On(); } }
        public string Detail { get => _detail; set { _detail = value; On(); } }
        public string Error { get => _error; set { _error = value; On(); } }
        /// <summary>0~1；小于 0 表示不确定进度</summary>
        public double Progress { get => _progress; set { _progress = Math.Max(-1, Math.Min(1, value)); On(); On(nameof(ProgressText)); On(nameof(Indeterminate)); } }
        public long Speed { get => _speed; set { _speed = value; On(); On(nameof(SpeedText)); } }
        public TaskState State { get => _state; set { _state = value; On(); On(nameof(IsActive)); On(nameof(StateText)); } }

        public bool IsActive => State == TaskState.Running;
        public bool Indeterminate => _progress < 0;
        public string ProgressText => _progress < 0 ? "" : $"{_progress * 100:0}%";
        public string SpeedText => _speed > 0 ? Util.FormatSize(_speed) + "/s" : "";
        public string StateText => State switch
        {
            TaskState.Done => "已完成",
            TaskState.Failed => "失败",
            TaskState.Canceled => "已取消",
            _ => "进行中"
        };

        public void Report(double progress, string detail = null)
        {
            Progress = progress;
            if (detail != null) Detail = detail;
        }

        public void Cancel() { try { Cts.Cancel(); } catch { } }
    }

    public static class TaskManager
    {
        public static ObservableCollection<LauncherTask> Tasks { get; } = new ObservableCollection<LauncherTask>();
        public static Dispatcher Dispatcher { get; set; }

        /// <summary>任务结束（成功/失败/取消）时触发，在 UI 线程上</summary>
        public static event Action<LauncherTask> Finished;
        public static event Action<LauncherTask> Added;

        public static LauncherTask Start(string title, Func<LauncherTask, Task> body)
        {
            var t = new LauncherTask { Title = title, Progress = -1, Detail = "准备中…" };
            OnUi(() => { Tasks.Insert(0, t); Added?.Invoke(t); });
            Task.Run(async () =>
            {
                try
                {
                    await body(t).ConfigureAwait(false);
                    t.Progress = 1;
                    t.Detail = "完成";
                    t.State = TaskState.Done;
                    Log.Info($"任务完成：{title}");
                }
                catch (OperationCanceledException)
                {
                    t.State = TaskState.Canceled;
                    t.Detail = "已取消";
                }
                catch (Exception ex)
                {
                    var msg = Flatten(ex);
                    t.Error = msg;
                    t.Detail = msg;
                    t.State = TaskState.Failed;
                    Log.Error($"任务失败：{title}", ex);
                }
                t.Speed = 0;
                OnUi(() => Finished?.Invoke(t));
            });
            return t;
        }

        public static string Flatten(Exception ex)
        {
            if (ex is AggregateException ag && ag.InnerExceptions.Count > 0) ex = ag.InnerExceptions[0];
            while (ex is System.Reflection.TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            return ex.Message;
        }

        public static void OnUi(Action a)
        {
            if (Dispatcher == null || Dispatcher.CheckAccess()) a();
            else Dispatcher.BeginInvoke(a);
        }

        public static void ClearFinished()
        {
            for (int i = Tasks.Count - 1; i >= 0; i--)
                if (!Tasks[i].IsActive) Tasks.RemoveAt(i);
        }

    }
}
