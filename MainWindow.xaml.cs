using LibVLCSharp.Shared;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Xceed.Wpf.Toolkit;

namespace LibVLCSharp.WPF.Sample
{
    public partial class MainWindow : Window
    {
        String playlistFileName;
        //Boolean playlistIsPLP;          // playlist file was created by this program
        LibVLC _libVLC;
        MediaPlayer _mediaPlayer;
        Boolean ignoreScrubberValueChange;
        public ObservableCollection<Segment> segmentList;
        Segment _currentSegment,
                _selectedSegment;   // entry in segmentList
        Boolean playlistDirty = false;
        Boolean _currentSegmentDirty = false;
        Boolean _segmentLoaded = false;     // true after initial OnPlaying() for _currentSegment such that controls can be manipulated
        long? _initialTime = null,              // unused?
              _selectionStart = null, _selectionEnd = null;        // applied in OnLengthChanged
        Boolean isPreviewingStart = false, isPreviewingEnd = false;                // previewing segment start or end
        Boolean pauseOnTimeChange = false;  // used when playing seg automatically on selection
        Timer frameTimer;
        Timer scrubTimer;
        bool isScrubbing = false;
        long dragCompletedTime = -1;    // set to .Time of OnScrubrDragCompleted()
        double maxScrubrWidth;
        BitmapImage bitmapPlay, bitmapPause;
        const long PREVIEWDURATION = 1000;      // msec
        const int DEFAULT_FPS = 30;         // frames/sec as assume if _mediaPlayer.Fps==0
        const bool RELATIVE_PATHS = true;   // write paths in .M3U relative to folder of .M3U
        const int SCRUB_INTERVAL = 100;     // ms between updates to .Time while scrubbing
        const bool AUTO_KEEP_SEG_CHANGES_ON_SAVE = true;    // on File Save(As), always keep changes to current seg (ie, don't ask; just keep changes)

        public MainWindow()
        {
            bitmapPause = new BitmapImage();
            bitmapPause.BeginInit();
            bitmapPause.UriSource = new Uri("/Pause.png", UriKind.Relative);
            bitmapPause.EndInit();

            bitmapPlay = new BitmapImage();
            bitmapPlay.BeginInit();
            bitmapPlay.UriSource = new Uri("/Play.png", UriKind.Relative);
            bitmapPlay.EndInit();

            InitializeComponent();

            // Can preload segmentList for testing here:
            segmentList = new ObservableCollection<Segment>()
            {
                //new Segment(){Filepath="747.wmv", Start=11242, End=16672},
                //new Segment(){Filepath="jeans.mp4", Start=20000}
                //new Segment(){Filepath="dreams.wmv", Start=5000, Duration=5000}
            };

            this.DataContext = segmentList;

            //segmentList.Add(new Segment() { Filepath = "jeans.mp4", Start = 20000, Duration = 10000 });   // this works

            videoView.Loaded += VideoView_Loaded;
            scrubTimer = new Timer(OnScrubTimer);
        }

        void VideoView_Loaded(object sender, RoutedEventArgs e)
        {
            Scrubr.Width = ScrubScroll.ViewportWidth;   // changes if window width changes

            Core.Initialize();

            SegmentListView.ItemsSource = segmentList;

            _libVLC = new LibVLC();
            _mediaPlayer = new MediaPlayer(_libVLC);
            _mediaPlayer.EnableHardwareDecoding = true;

            videoView.MediaPlayer = _mediaPlayer;

            // callbacks should switch threads: https://code.videolan.org/videolan/LibVLCSharp/-/blob/3.x/docs/best_practices.md#do-not-call-libvlc-from-a-libvlc-event-without-switching-thread-first
            _mediaPlayer.MediaChanged += OnMediaChanged;
            _mediaPlayer.Opening += OnOpening;
            _mediaPlayer.LengthChanged += OnLengthChanged;
            _mediaPlayer.TimeChanged += OnTimeChanged;
            _mediaPlayer.SeekableChanged += OnSeekableChanged;
            _mediaPlayer.Paused += OnPaused;
            _mediaPlayer.Playing += OnPlaying;
            _mediaPlayer.Buffering += OnBuffering;
            _mediaPlayer.EndReached += OnEndReached;
            _mediaPlayer.Stopped += OnStopped;
            _mediaPlayer.EncounteredError += OnError;
            //_mediaPlayer.Forward += OnForward;  // never called
            //_mediaPlayer.PositionChanged += OnPositionChanged;  // not called after NextFrame(); may relate to position on screen

            //Scrubber.ValueChanged += OnScrubberValueChanged;
            //Scrubber.GotFocus += OnScrubberFocus;
            //Scrubber.IsSelectionRangeEnabled = true;
            //Scrubber.SelectionStart = 0;
            Scrubr.ValueChanged += OnScrubrValueChanged;
            Scrubr.GotFocus += OnScrubrFocus;
            Scrubr.IsSelectionRangeEnabled = true;
            Scrubr.SelectionStart = 0;

            string[] args = Environment.GetCommandLineArgs();
            bool isOpen = false;
            if (args.Length == 2) isOpen = Open(args[1]);
            if (!isOpen) New_Executed(null, null);  // if filename not specified on command line, or couldn't open

            /*if (segmentList.Count > 0) {
                _selectedSegment = segmentList[0];
                SegmentListView.SelectedIndex = 0;      // this causes automatic LoadNewSegment(_selectedSegment)
            }

            DataGridRow row = (DataGridRow)SegmentListView.ItemContainerGenerator.ContainerFromIndex(0);
            if (row != null)
            {
                row.IsSelected = true;
                DataGridCell cell = GetCell(SegmentListView, row, 0);
                if (cell != null) cell.Focus();
            }

            SetPlaylistDirty(false);*/
        }

        private void OnError(object sender, EventArgs e)
        {
            System.Windows.MessageBox.Show("Can't play", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        public static DataGridCell GetCell(DataGrid dataGrid, DataGridRow rowContainer, int column) {
            if (rowContainer != null) {
                DataGridCellsPresenter presenter = FindVisualChild<DataGridCellsPresenter>(rowContainer);
                if (presenter == null) {
                    /* if the row has been virtualized away, call its ApplyTemplate() method
                     * to build its visual tree in order for the DataGridCellsPresenter
                     * and the DataGridCells to be created */
                    rowContainer.ApplyTemplate();
                    presenter = FindVisualChild<DataGridCellsPresenter>(rowContainer);
                }
                if (presenter != null) {
                    #pragma warning disable IDE0019 // Use pattern matching
                    DataGridCell cell = presenter.ItemContainerGenerator.ContainerFromIndex(column) as DataGridCell;
                    #pragma warning restore IDE0019 // Use pattern matching
                    if (cell == null) {
                        /* bring the column into view
                         * in case it has been virtualized away */
                        dataGrid.ScrollIntoView(rowContainer, dataGrid.Columns[column]);
                        cell = presenter.ItemContainerGenerator.ContainerFromIndex(column) as DataGridCell;
                    }
                    return cell;
                }
            }
            return null;
        }

        public static T FindVisualChild<T>(DependencyObject obj) where T : DependencyObject {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(obj); i++) {
                DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(obj, i);
                if (child != null && child is T)
                    return (T)child;
                else {
                    T childOfChild = FindVisualChild<T>(child);
                    if (childOfChild != null)
                        return childOfChild;
                }
            }
            return null;
        }

        private void OnOpening(object sender, EventArgs e)
        {
            Debug.WriteLine("OnOpening()");
        }

        private void OnMediaChanged(object sender, MediaPlayerMediaChangedEventArgs e)
        {
            Debug.WriteLine("OnMediaChanged()");
        }

        /*private void SetMedia()
        {
            // Loads from _currentSegment.
            Media m = new Media(_libVLC, _currentSegment.Filename, FromType.FromPath);
            //m.AddOption("rate=0.5");          // works, but makes scrubbing cumbersome
            //m.AddOption("start-time=11.0");   // works, but makes scrubbing cumbersome
            //m.AddOption("stop-time=12.0");   // works, but makes scrubbing cumbersome
            _mediaPlayer.Play(m);       // can hang on segment change

            //_mediaPlayer.Play(new Media(_libVLC, "jeans.mp4", FromType.FromPath));
            //_mediaPlayer.Play(new Media(_libVLC, "747.wmv", FromType.FromPath));
        }*/

        private void OnStopped(object sender, EventArgs e)
        {
            // Will be called straight after a segment is loaded into _mediaPlayer if using start-paused
            Debug.WriteLine("OnStopped()");
            /*this.Dispatcher.Invoke(() =>
            {
                //_mediaPlayer.Stop();
                _mediaPlayer.Play();
            });*/
        }

        public delegate void reloadSegmentDelegateType(MainWindow _this);  // type definition of delegate

        static private void ReloadSegment(MainWindow _this) {
            Debug.WriteLine("ReloadSegment()");

            _this.LoadCurrentSegment(true);

            /*_this._initialTime = _this._selectionStart = (long)_this.Scrubber.SelectionStart;    // Scrubber will be updated in OnLengthChanged
            _this._selectionEnd = (long)_this.Scrubber.SelectionEnd;    // Scrubber will be updated in OnLengthChanged
            //Media m = new Media(_this._libVLC, _this.filename, FromType.FromPath);
            //m.AddOption("rate=0.5");          // works, but makes scrubbing cumbersome
            //m.AddOption("start-time=11.0");   // works, but makes scrubbing cumbersome
            //m.AddOption("stop-time=12.0");   // works, but makes scrubbing cumbersome
            //_this._mediaPlayer.Play(m);
            _this.SetMedia();
            _this._mediaPlayer.SetPause(true);
            _this.DisplayTime(_this._initialTime??0);*/
        }

        reloadSegmentDelegateType reloadSegmentDelegate = new reloadSegmentDelegateType(ReloadSegment);         // instantiation of a delegate that points to ReloadSegment

        private void OnEndReached(object sender, EventArgs e) {
            Debug.WriteLine("OnEndReached()");
            SetPauseBtn(true);
            DisplayTimeThreadSafe(_mediaPlayer.Length);
            SetSegmentLoaded(false);

            /*this.Dispatcher.Invoke(() =>
            {
                //_mediaPlayer.Stop();
                //_mediaPlayer.Play();
            });*/

            // Reload media here and now
            this.Dispatcher.BeginInvoke(reloadSegmentDelegate, this);
        }

        private void OnPlaying(object sender, EventArgs e)
        {
            Debug.WriteLine("OnPlaying(): state={0}", _mediaPlayer.State);
            if (!_segmentLoaded && !_currentSegment.IsEmpty) SetSegmentLoaded(true);
            SetPauseBtn(false);
        }

        private void SetSegmentLoaded(Boolean loaded) {
            // Configure segment controls IAW loaded and _currentSegment.
            // load placeholder.png into _mediaPlayer, in case of right-click on segmentList?
            _segmentLoaded = loaded;

            /*if (loaded)     // pause and reset time to start of segment
            {
                ThreadPool.QueueUserWorkItem(_ => {
                    _mediaPlayer.Pause();
                    //_mediaPlayer.Time = _currentSegment.Start;
                });
            }*/

            this.Dispatcher.Invoke(() => {
                // Possibly more elegant way to enable/disable: https://stackoverflow.com/questions/2906346/disable-button-in-wpf
                /*Scrubber.IsEnabled =*/ Scrubr.IsEnabled = PauseBtn.IsEnabled = BackBtn.IsEnabled = FrameBtn.IsEnabled = GoToStartBtn.IsEnabled = GoToEndBtn.IsEnabled = StartBtn.IsEnabled = 
                    EndBtn.IsEnabled = TrimEndBtn.IsEnabled = ExtendEndBtn.IsEnabled = PreviewStartBtn.IsEnabled = PreviewEndBtn.IsEnabled = TimeLabel.IsEnabled = FilenameLabel.IsEnabled = OpenBtn.IsEnabled = Count.IsEnabled = 
                    SlowBox.IsEnabled = SeekBox.IsEnabled = MuteBox.IsEnabled = loaded;
                FilenameLabel.Content = loaded? _currentSegment.Filename : null;
                enableScrubberZoomButtons();
            });
        }

        private void SetPauseBtn(bool play)
        {
            this.Dispatcher.Invoke(() =>
            {
                DumpThread("SetPauseBtn");
                PauseBtn.ToolTip = play? "Play (P)" : "Pause (P)";
                Image image = (Image)PauseBtn.Content;
                image.Source = play? bitmapPlay : bitmapPause;
            });
        }

        /*private void OnScrubberFocus(object sender, RoutedEventArgs e)  // user probably clicked on scrubber to change its value
        {
            Debug.WriteLine("OnScrubberFocus()");
            _mediaPlayer.SetPause(true);
        }*/

        private void OnScrubrFocus(object sender, RoutedEventArgs e)  // user probably clicked on scrubber to change its value
        {
            // Gets called AFTER the corresponding OnScrubrValueChanged() event
            Debug.WriteLine("OnScrubrFocus()");
            //_mediaPlayer.SetPause(true);      // reinstate to pause playback when scrubber is clicked
        }

        private void OnBuffering(object sender, MediaPlayerBufferingEventArgs e)
        {
            //Debug.WriteLine("OnBuffering(): " + e.Cache);
        }

        /*private void OnForward(object sender, EventArgs e) {  // never seems to get called
            Debug.WriteLine("OnForward(): ");
        }*/

        private void OnLengthChanged(object sender, MediaPlayerLengthChangedEventArgs e)
        // will be called with e.Length==10000 if loading placeholder (ie, no actual seg file)
        {
            Debug.WriteLine("OnLengthChanged(): length={0}, fps={1}",e.Length, _mediaPlayer.Fps);

            maxScrubrWidth = 2.5 * e.Length;

            if (e.Length == 0) {
                System.Windows.MessageBox.Show("Video seems to have zero length", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                this.Dispatcher.BeginInvoke(closeProgramDelegate, this);
            }

            this.Dispatcher.Invoke(() =>
            {
                enableScrubberZoomButtons();

                ignoreScrubberValueChange = true;
                //Scrubber.SelectionEnd = Scrubber.Maximum = e.Length;
                Scrubr.SelectionEnd = Scrubr.Maximum = e.Length;
                if (_selectionStart != null) {
                    SetSelectionStart((double)_selectionStart);
                    SetSelectionEnd((double)_selectionEnd);
                    _selectionStart = _selectionEnd = null;
                    if (_initialTime != null) {
                        this.Dispatcher.BeginInvoke(setMediaPlayerTimeDelegate, this, _initialTime);
                        _initialTime = null;
                    }
                }
                if (_initialTime != null) {
                    this.Dispatcher.BeginInvoke(setMediaPlayerTimeDelegate, this, _initialTime);

                    _initialTime = null;
                }

                // Ensure thumb (presumed to be at currentSegment.Start) is visible:
                /*double timeProportion = (double)_currentSegment.Start / e.Length;
                double lhsProportion = ScrubScroll.HorizontalOffset / ScrubScroll.ExtentWidth;
                double rhsProportion = (ScrubScroll.HorizontalOffset + ScrubScroll.ViewportWidth) / ScrubScroll.ExtentWidth;
                double newOffset = timeProportion * Scrubr.Width - ScrubScroll.ViewportWidth / 2;
                if (timeProportion < lhsProportion || timeProportion > rhsProportion)
                    ScrubScroll.ScrollToHorizontalOffset(newOffset);*/
                if (_currentSegment != null) ScrollThumbIntoView(_currentSegment.Start);

                ignoreScrubberValueChange = false;
            });
        }

        public delegate void setMediaPlayerTimeDelegateType(MainWindow _this, long time);  // type definition of delegate
        public delegate void mainWindowDelegateType(MainWindow _this);  // type definition of delegate
        mainWindowDelegateType closeProgramDelegate = new mainWindowDelegateType(CloseProgram);

        static private void CloseProgram(MainWindow _this) {
            _this.Close();
        }

        static private void SetMediaPlayerTime(MainWindow _this, long time)
        {
            Debug.WriteLine("SetMediaPlayerTime() time={0} current={1}", time, _this._mediaPlayer.Time);
            _this._mediaPlayer.Time = time;
        }

        setMediaPlayerTimeDelegateType setMediaPlayerTimeDelegate = new setMediaPlayerTimeDelegateType(SetMediaPlayerTime);         // instantiation of a delegate that points to SetMediaPlayerTime

        private void OnPaused(object sender, EventArgs e)   // when first paused (eg, just after loading), can read fps and other things
        {
            Debug.WriteLine("OnPaused(): fps=" + _mediaPlayer.Fps + " state=" + _mediaPlayer.State + " time=" + _mediaPlayer.Time);
            DumpThread("OnPaused");
            foreach (var track in _mediaPlayer.Media.Tracks) {
                if (track.TrackType == TrackType.Video) {
                    Debug.WriteLine("OnPaused(): FrameRateNum={0} FrameRateDen={1}", track.Data.Video.FrameRateNum, track.Data.Video.FrameRateDen);
                }
            }
            SetPauseBtn(true);

            /*this.Dispatcher.Invoke(() =>
            {
                //_mediaPlayer.Time = _currentSegment.Start;  // crash
                //GoToTime(_currentSegment.Start);    // crash
            });*/

            //this.Dispatcher.BeginInvoke(setMediaPlayerTimeDelegate, this, _currentSegment.Start);     // only try this on first loading seg
        }

        private void OnSeekableChanged(object sender, MediaPlayerSeekableChangedEventArgs e)
        {
            Debug.WriteLine("OnSeekableChanged(): seekable="+ e.Seekable);
        }

        /*private void OnScrubberDragStarted(object sender, DragStartedEventArgs e)   // user grabs scrubber to drag it
        {
            Debug.WriteLine("OnScrubberDragStarted()");
            _mediaPlayer.SetPause(true);
        }*/

        private void OnScrubrDragStarted(object sender, DragStartedEventArgs e)   // user grabs scrubber to drag it
        {
            Debug.WriteLine("OnScrubrDragStarted()");
            isScrubbing = true;
            _mediaPlayer.SetPause(true);

            scrubTimer.Change(0, SCRUB_INTERVAL);
        }
        private void OnScrubrDragCompleted(object sender, DragCompletedEventArgs e)   // user grabs scrubber to drag it
        {
            Debug.WriteLine("OnScrubrDragCompleted()");
            isScrubbing = false;
            dragCompletedTime = _mediaPlayer.Time;
        }

        private void OnScrubTimer(Object obj) {
            Debug.WriteLine("OnScrubTimer()");

            this.Dispatcher.Invoke(() =>
            {
                long time = dragCompletedTime >= 0? dragCompletedTime : (long)Scrubr.Value;

                if (dragCompletedTime >= 0)
                {
                    scrubTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    dragCompletedTime = -1;
                }
                //Debug.WriteLine("OnScrubrValueChanged(): setting .Time to {0}", time);
                //DumpThread("OnScrubrValueChanged");
                if (_mediaPlayer.Time != time)
                {
                    _mediaPlayer.Time = time;   // can hang here? might need ignoreScrubrValueChange somewhere else
                    // Alternative ways to scrub (that don't work any better):
                    //TimeSpan timespan = new TimeSpan(time * 10000); _mediaPlayer.SeekTo(timespan);
                    //_mediaPlayer.Position = (float)(time / Scrubr.Maximum);
                    //Debug.WriteLine("OnScrubrValueChanged() .Time=" + _mediaPlayer.Time);

                    UpdateTimeLabel(time);
                }
            });
        }

        /*private void OnScrubberValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_mediaPlayer.IsPlaying || ignoreScrubberValueChange) return;
            Debug.WriteLine("OnScrubberValueChanged(): isStepping={0}", ignoreScrubberValueChange);
            //ignoreScrub = true;
            //_mediaPlayer.Play();
            long time = (long)Scrubber.Value;
            Debug.WriteLine("OnScrubberValueChanged(): setting _mediaPlayer.Time from Scrubber.Value: {0}", time);
            _mediaPlayer.Time = time;   // can hang here? might need ignoreScrubberValueChange somewhere else

            //TimeLabel.Content = (new DateTime(time * 10000)).ToString("HH:mm:ss.fff");      // TODO 2.5 do this everywhere that TimeLabel is changed
            UpdateTimeLabel(time);

            //_mediaPlayer.SetPause(true);
            //ignoreScrub = false;
            Debug.WriteLine("OnScrubberValueChanged() time=" + _mediaPlayer.Time);
        }*/

        private void OnScrubrValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {

            //Debug.WriteLine("OnScrubrValueChanged(): isPlaying={0} ignore={1} value={2} .Time={3}", _mediaPlayer.IsPlaying, ignoreScrubberValueChange, Scrubr.Value, _mediaPlayer.Time);
            if (/*_mediaPlayer.IsPlaying ||*/ ignoreScrubberValueChange) return;
            //ignoreScrub = true;
            //_mediaPlayer.Play();
            long time = (long)Scrubr.Value;
            //Debug.WriteLine("OnScrubrValueChanged(): setting .Time to {0}", time);
            //DumpThread("OnScrubrValueChanged");
            if (!isScrubbing) {
                _mediaPlayer.Time = time;   // can hang here? might need ignoreScrubrValueChange somewhere else
                // Alternative ways to scrub (that don't work any better):
                //TimeSpan timespan = new TimeSpan(time * 10000); _mediaPlayer.SeekTo(timespan);
                //_mediaPlayer.Position = (float)(time / Scrubr.Maximum);
                //Debug.WriteLine("OnScrubrValueChanged() .Time=" + _mediaPlayer.Time);

                UpdateTimeLabel(time); 
            }

            // If thumb is dragged off side of ScrubScroll, scroll it back into visible extent:
            double timeProportion = (double)time / _mediaPlayer.Length;
            double lhsProportion = ScrubScroll.HorizontalOffset / ScrubScroll.ExtentWidth;
            double rhsProportion = (ScrubScroll.HorizontalOffset + ScrubScroll.ViewportWidth) / ScrubScroll.ExtentWidth;
            Debug.WriteLine("OnScrubrValueChanged(): timeProportion={0} lhs={1} rhs={2} HorizontalOffset={3}", timeProportion, lhsProportion, rhsProportion, ScrubScroll.HorizontalOffset);
            if (isScrubbing)
            {
                if (timeProportion < lhsProportion)
                    ScrubScroll.ScrollToHorizontalOffset(ScrubScroll.HorizontalOffset - 10);
                else if (timeProportion > rhsProportion)
                    ScrubScroll.ScrollToHorizontalOffset(ScrubScroll.HorizontalOffset + 10);
            }
            else     // position thumb in centre; cf. ScrollThumbIntoView(time)
            {
                
                if (timeProportion < lhsProportion || timeProportion > rhsProportion)
                {
                    double newOffset = timeProportion * Scrubr.Width - ScrubScroll.ViewportWidth / 2;
                    ScrubScroll.ScrollToHorizontalOffset(newOffset);
                }
            }

            //_mediaPlayer.SetPause(true);
            //ignoreScrub = false;
        }
        private void OnTimeChanged(object sender, MediaPlayerTimeChangedEventArgs e)    // only called during play, not scrubbing or NextFrame()
        {
            //return;
            Debug.WriteLine("OnTimeChanged()");
            //if (ignoreScrub) return;
            //this.Dispatcher.Invoke(() => { DisplayTime(e.Time); });
            DisplayTimeThreadSafe(e.Time);

            // Scroll scrubber to keep thumb visible:
            this.Dispatcher.Invoke(() => {
                double timeProportion = (double)e.Time / _mediaPlayer.Length;
                double rhsProportion = (ScrubScroll.HorizontalOffset + ScrubScroll.ViewportWidth) / ScrubScroll.ExtentWidth;
                if (timeProportion > rhsProportion)
                    ScrubScroll.ScrollToHorizontalOffset(timeProportion * ScrubScroll.ExtentWidth + 10);

                /*if (pauseOnTimeChange)
                {
                    pauseOnTimeChange = false;
                    Debug.WriteLine("OnTimeChanged(): calling .Pause()");
                    DumpThread("OnTimeChanged");
                    _mediaPlayer.Pause();
                    //_mediaPlayer.Time = _currentSegment.Start;    // crash
                }*/
            });

            if (pauseOnTimeChange)
            {
                pauseOnTimeChange = false;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Debug.WriteLine("OnTimeChanged(): calling .Pause()");
                    DumpThread("OnTimeChanged");
                    _mediaPlayer.Pause();
                    //GoToTime(_currentSegment.Start);
                    //_mediaPlayer.Time = _currentSegment.Start; 
                    // setting time to start doesn't work; cf. onEndReached, previewStart(?)
                });
            }

            //_mediaPlayer.SetPause(true);
            //Dispatcher.BeginInvoke(SetMediaPlayerTimeDelegate, this, 11000);
        }

        /*private void GoToBtn_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("GoToBtn");
            _mediaPlayer.Time = (long)Scrubber.SelectionStart;
            DisplayTime(_mediaPlayer.Time);
            //TimeLabel.Content = _mediaPlayer.Time;
        }*/

        private void FrameBtn_Click(object sender, RoutedEventArgs e) {
            // After preview, first press of Frame sets time to 0; next press proceeds correctly. Zooming scrubber zooms to 0 rather than thumb...
            // Seems to be a bug with VLC MediaPlayer.Time
            PreviewStop();
            long timeBefore = _mediaPlayer.Time;
            Debug.WriteLine("FrameBtn_Click(): state=" + _mediaPlayer.State + " time=" + timeBefore + " calling _mediaPlayer.NextFrame()");
            _mediaPlayer.NextFrame();
            Debug.WriteLine("FrameBtn_Click(): after NextFrame(): state=" + _mediaPlayer.State + " time={0}", _mediaPlayer.Time);
            //DisplayTime(_mediaPlayer.Time);
            //TimeLabel.Content = _mediaPlayer.Time;
            //ScrollThumbIntoView(_mediaPlayer.Time);

            frameTimer = new Timer(OnFrameTimer, (timeBefore, DateTime.Now.Ticks + 10000000), 10, 50);  // poll .Time until it changes
        }

        private void OnFrameTimer(Object obj) {
            var stopTimes = ((long timeBefore, long timeout)) obj;
            Debug.WriteLine("OnFrameTimer({0}): {1}, {2}", stopTimes, _mediaPlayer.Time, DateTime.Now.Ticks - stopTimes.timeout);
            if (_mediaPlayer.Time != stopTimes.timeBefore || DateTime.Now.Ticks >= stopTimes.timeout) frameTimer.Dispose();
            if (_mediaPlayer.Time != stopTimes.timeBefore) DisplayTimeThreadSafe(_mediaPlayer.Time, true);
            // Could create onFrame() synthetic event (like TimeChanged).
        }

        private void BackBtn_Click(object sender, RoutedEventArgs e) {
            Debug.WriteLine("BackBtn_Click(): time is {0}", _mediaPlayer.Time);
            PreviewStop();

            long currentTime = _mediaPlayer.Time;
            long newTime = (long)Math.Max(currentTime - GetFrameDuration(), 0);
            _mediaPlayer.Time = newTime;    // on initial load, and after OnEndReached, doesn't result in updated vid position unless vid is played first. Can't set Time on load.
            DisplayTime(_mediaPlayer.Time);
            Debug.WriteLine("BackBtn_Click(): _mediaPlayer.Time is {0}", _mediaPlayer.Time);
            //TimeLabel.Content = _mediaPlayer.Time;

            ScrollThumbIntoView(_mediaPlayer.Time);
        }

        private void PauseBtn_Click(object sender, RoutedEventArgs e) {
            Debug.WriteLine("PauseBtn_Click(): state=" + _mediaPlayer.State);
            _mediaPlayer.Pause();
            // TimeLabel.Content = _mediaPlayer.Time;
            DisplayTime(_mediaPlayer.Time);
            //Debug.WriteLine("fps=" + _mediaPlayer.Fps);
            PreviewStop();
        }

        private void DisplayTime(long time) {       // Updates time elements in UI, but doesn't change media time position
            Debug.WriteLine("DisplayTime({0}): setting Scrubr.Value", time);
            //TimeLabel.Content = time;
            UpdateTimeLabel(time);
            ignoreScrubberValueChange = true;
            //Scrubber.Value = time;      // will result in call to OnScrubberValueChanged()
            Scrubr.Value = time;      // will result in call to OnScrubrValueChanged()
            ignoreScrubberValueChange = false;
        }

        private void DisplayTimeThreadSafe(long time, bool scrollThumbIntoView = false) {
            this.Dispatcher.Invoke(() => { 
                DisplayTime(time);
                if (scrollThumbIntoView) ScrollThumbIntoView(time);
            });
        }

        private void UpdateTimeLabel(long time)
        {
            TimeLabel.Content = MsecToString(time);
        }

        static public string MsecToString(long time)
        {
            return (new DateTime(time * 10000)).ToString("H:mm:ss.fff");
        }

        private void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            //SetSelectionStart(Scrubber.Value);
            PreviewStop();
            SetSelectionStart(Scrubr.Value);
            SetCurrentSegmentDirty(true);

            ScrollThumbIntoView(Scrubr.Value);
        }

        public void SetSelectionStart(double start)
        {
            //if (start >= Scrubber.SelectionEnd) UpdateSegmentEnd(Scrubber.Maximum);
            if (start >= Scrubr.SelectionEnd) UpdateSegmentEnd(Scrubr.Maximum, false);
            UpdateSegmentStart(start);
        }

        private void UpdateSegmentStart(double start)
        {
            //Scrubber.SelectionStart = start;
            Scrubr.SelectionStart = start;
            _currentSegment.Start = (long)start;
        }

        private void UpdateSegmentEnd(double end, bool setEnd)
        {
            //Scrubber.SelectionEnd = end;
            Scrubr.SelectionEnd = end;
            _currentSegment.End = (long)end;
            _currentSegment.EndSet = setEnd;
        }

        private void EndBtn_Click(object sender, RoutedEventArgs e)     // set end of segment
        {
            PreviewStop();

            double end = 0; // 0 signifies end of clip (so setEnd will be false)

            if (Scrubr.Value != Scrubr.Maximum)
            {
                // Go back by one frame (assuming that user has paused clip at first frame beyond desired segment end:
                Scrubr.Value -= GetFrameDuration();     // doesn't take IsSegmentSlow() into account
                end = Scrubr.Value;
            }

            //SetSelectionEnd(Scrubber.Value);
            SetSelectionEnd(end);  // if this is end of scrubber (end==0), setEnd should be false
            SetCurrentSegmentDirty(true);

            ScrollThumbIntoView(Scrubr.Value);
        }

        private float GetFps()
        {
            return (_mediaPlayer.Fps > 0 ? _mediaPlayer.Fps : DEFAULT_FPS);
        }

        private float GetFrameDuration() // duration of one frame (ms)
        {
            return 1000 / GetFps();
        }

        private bool IsSegmentSlow()    // true if segment is explicitly slow or if playlist is slow
        {
            return SlowBox.IsChecked != null ? (bool)SlowBox.IsChecked : (bool)PlaylistSlow.IsChecked;
        }
        private bool IsSegmentFastSeek()    // true if segment is explicitly fastSeek or if playlist is fastSeek
        {
            return SeekBox.IsChecked != null ? (bool)SeekBox.IsChecked : (bool)PlaylistFastSeek.IsChecked;
        }

        private bool IsSegmentMute()    // true if segment is explicitly mute or if playlist is mute
        {
            return MuteBox.IsChecked != null ? (bool)MuteBox.IsChecked : (bool)PlaylistMute.IsChecked;
        }

        private void SetSelectionEnd(double end) {
            // If end == 0, use end of clip
            bool setEnd = end > 0;
            if (end == 0) end = Scrubr.Maximum;
            //if (end <= Scrubber.SelectionStart) UpdateSegmentStart(0);
            if (end <= Scrubr.SelectionStart) UpdateSegmentStart(0);
            UpdateSegmentEnd(end, setEnd);
        }

        private void GoToStartBtn_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("GoToStartBtn_Click(): state=" + _mediaPlayer.State);
            PreviewStop();
            GoToTime((long)Scrubr.SelectionStart);
            /*_mediaPlayer.Time = (long)Scrubr.SelectionStart;
            DisplayTime(_mediaPlayer.Time);
            ScrollThumbIntoView(_mediaPlayer.Time);*/
        }

        private void GoToEndBtn_Click(object sender, RoutedEventArgs e)
        {
            PreviewStop();
            GoToTime((long)Scrubr.SelectionEnd);
            /*_mediaPlayer.Time = (long)Scrubr.SelectionEnd;
            DisplayTime(_mediaPlayer.Time);
            ScrollThumbIntoView(_mediaPlayer.Time);*/
        }

        private void GoToTime(long time) {
            Debug.WriteLine("GoToTime({0})", time);
            _mediaPlayer.Time = time;
            DisplayTime(_mediaPlayer.Time);
            ScrollThumbIntoView(_mediaPlayer.Time);
        }

        private void ScrollThumbIntoView(double time) {
            double timeProportion = time / _mediaPlayer.Length;
            double lhsProportion = ScrubScroll.HorizontalOffset / ScrubScroll.ExtentWidth;
            double rhsProportion = (ScrubScroll.HorizontalOffset + ScrubScroll.ViewportWidth) / ScrubScroll.ExtentWidth;
            if (timeProportion < lhsProportion || timeProportion > rhsProportion)
            {
                double newOffset = timeProportion * Scrubr.Width - ScrubScroll.ViewportWidth / 2;
                ScrubScroll.ScrollToHorizontalOffset(newOffset);
            }
        }

        private void OnSegmentSelection(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            Debug.WriteLine("OnSegmentSelection(): added={0} removed={1} selected={2}", e.AddedItems.Count, e.RemovedItems.Count, ((DataGrid)sender).SelectedItems.Count);

            // If e.RemovedItems.Count, save prev seg if it was changed (dirty):
            if (e.RemovedItems.Count == 1)
            {
                CheckKeepSegmentChanges(false);  // probably redundant because CheckKeepSegmentChanges() should have been called in preview handlers.
                SetSegmentLoaded(false);
            }

            DataGrid grid = (DataGrid)sender;
            if (grid.SelectedItems.Count != 1) return;

            ClearCurrentSegment();
            _selectedSegment = (Segment)grid.SelectedItem;
            LoadNewSegment(_selectedSegment);
        }

        private void ClearCurrentSegment() {
            videoView.Visibility = Visibility.Hidden;
            _currentSegment = _selectedSegment = null;
            _selectionStart = _selectionEnd = _initialTime = null;
            FilenameLabel.Content = null;
            TimeLabel.Content = null;
            Count.Value = 1;
            SlowBox.IsChecked = SeekBox.IsChecked = MuteBox.IsChecked = null;
            Scrubr.Value = Scrubr.SelectionStart = Scrubr.SelectionEnd = 0;
            SetSegmentLoaded(false);
            SetCurrentSegmentDirty(false);
        }

        private void DebugBtn_Click(object sender, RoutedEventArgs e)
        {
        }

        private void PreviewStartBtn_Click(object sender, RoutedEventArgs e)
        {
            if (isPreviewingStart) {
                PreviewStop();
                return;
            }

            if (isPreviewingEnd) PreviewStop(false);

            isPreviewingStart = true;
            PreviewStartBtn.Background = System.Windows.Media.Brushes.LightGreen;
            PreviewModeButtons(false);

            Media media = LoadMedia();
            if (_currentSegment.Start > 0) media.AddOption("start-time=" + 0.001*_currentSegment.Start);    // works, but makes scrubbing cumbersome
            if (_currentSegment.End > 0) {
                long end = _currentSegment.Start + PREVIEWDURATION;
                if (end > _currentSegment.End) end = _currentSegment.End;
                media.AddOption("stop-time=" + 0.001*end);
            }
            if (IsSegmentSlow()) media.AddOption("rate=0.5");                                          // works, but makes scrubbing cumbersome
            if (IsSegmentFastSeek()) media.AddOption("input-fast-seek");
            if (IsSegmentMute()) media.AddOption("no-audio");
            media.AddOption("input-repeat=999");
            //Debug.WriteLine("PreviewBtn_Click(): start-time=" + 0.001*_currentSegment.Start);
            //Debug.WriteLine("PreviewBtn_Click(): stop-time=" + _currentSegment.End / 1000.0);
            //media.AddOption("start-time=11.242"); media.AddOption("stop-time=11.672");

            // TODO 5 seg brightness and contrast:
            /*_mediaPlayer.SetAdjustInt(VideoAdjustOption.Enable, 1);
            _mediaPlayer.SetAdjustFloat(VideoAdjustOption.Contrast, 2f);
            _mediaPlayer.SetAdjustFloat(VideoAdjustOption.Brightness, 2f);*/

            Debug.WriteLine("PreviewStartBtn_Click(): calling Play() state={0}", _mediaPlayer.State);
            _mediaPlayer.Play(media);
        }

        private void PreviewEndBtn_Click(object sender, RoutedEventArgs e) {
            if (isPreviewingEnd) {
                PreviewStop();
                return;
            }

            if (isPreviewingStart) PreviewStop(false);

            isPreviewingEnd = true;
            PreviewEndBtn.Background = System.Windows.Media.Brushes.LightGreen;
            PreviewModeButtons(false);

            Media media = LoadMedia();
            long end = _currentSegment.Duration;
            if (_currentSegment.End > 0) {
                end = _currentSegment.End;
                media.AddOption("stop-time=" + 0.001 * end);         // works, but makes scrubbing cumbersome
            }
            long start = end - PREVIEWDURATION;
            if (start < 0) start = 0;
            if (start > 0) {
                media.AddOption("start-time=" + 0.001 * start);
            }
            if (IsSegmentSlow()) media.AddOption("rate=0.5");                                          // works, but makes scrubbing cumbersome
            if (IsSegmentFastSeek()) media.AddOption("input-fast-seek");
            if (IsSegmentMute()) media.AddOption("no-audio");
            media.AddOption("input-repeat=999");
            //Debug.WriteLine("PreviewBtn_Click(): start-time=" + 0.001*_currentSegment.Start);
            //Debug.WriteLine("PreviewBtn_Click(): stop-time=" + _currentSegment.End / 1000.0);
            //media.AddOption("start-time=11.242"); media.AddOption("stop-time=11.672");
            Debug.WriteLine("PreviewEndBtn_Click(): calling Play(); state={0}", _mediaPlayer.State);
            _mediaPlayer.Play(media);
        }

        private void PreviewModeButtons(Boolean enable) {
            Scrubr.IsEnabled = PauseBtn.IsEnabled = BackBtn.IsEnabled = FrameBtn.IsEnabled = GoToStartBtn.IsEnabled = GoToEndBtn.IsEnabled = StartBtn.IsEnabled =
                    EndBtn.IsEnabled = TimeLabel.IsEnabled = FilenameLabel.IsEnabled = OpenBtn.IsEnabled = Count.IsEnabled =
                    SlowBox.IsEnabled = SeekBox.IsEnabled = MuteBox.IsEnabled = /*TrimEndBtn.IsEnabled =*/ enable;
            SaveSegment.IsEnabled = UndoSegment.IsEnabled = enable && _currentSegmentDirty;
        }

        private void PreviewStop(Boolean reload = true) {
            if (!isPreviewingStart && !isPreviewingEnd) return; // nothing to do

            //if (pause && _mediaPlayer.IsPlaying) _mediaPlayer.Pause();
            PreviewStartBtn.Background = PreviewEndBtn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xDD, 0xDD, 0xDD));
            isPreviewingStart = isPreviewingEnd = false;

            PreviewModeButtons(true);

            if (reload) LoadCurrentSegment(true);   // don't need to do this if we're going to start previewing again immediately
        }

        private void LoadNewSegment(Segment segment)
        {
            _currentSegment = new Segment(segment);
            FilenameLabel.Content = _currentSegment.Filename;
            Count.Value = _currentSegment.Count;
            SlowBox.IsChecked = _currentSegment.Slow;
            SeekBox.IsChecked = _currentSegment.FastSeek;
            MuteBox.IsChecked = _currentSegment.Mute;
            SetCurrentSegmentDirty(false);
            ScrubberZoomAll_Click(null, null);
            LoadCurrentSegment(_currentSegment.IsEmpty);
            /*//filename = segment.Filename;
            SetMedia();
            _mediaPlayer.SetPause(true);
            _mediaPlayer.Time = segment.Start;
            Debug.WriteLine("LoadNewSegment(): Time={0}", _mediaPlayer.Time);
            DisplayTime(segment.Start);
            _selectionStart = segment.Start;    // Scrubber will be updated in OnLengthChanged
            _selectionEnd = segment.End;    // Scrubber will be updated in OnLengthChanged
            */
        }

        private void SegmentListView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
            PreviewStop(true);

            // How to cancel DataGrid selection:
            //      https://stackoverflow.com/questions/15545736/how-to-cancel-datagrid-selection-changed-event-in-wpf
            //      https://social.msdn.microsoft.com/Forums/vstudio/en-US/d09ce3b6-6a46-4e05-8d76-ed3de109d11c/wpf-4-datagrid-cancelling-selectionchange-restoring-previously-selected-row
            if (CheckKeepSegmentChanges(true)) {    // returns true if okay to continue (ie, operation wasn't cancelled)
                //SetSegmentLoaded(false);        // this was premature; do it when OnSegmentSelection() indicates deselection
                // ok to change selection, so reinvoke the click event:
                SegmentListView.Dispatcher.BeginInvoke(
                   new Action(() => {
                       RoutedEventArgs args = new MouseButtonEventArgs(e.MouseDevice, 0, e.ChangedButton) {
                           RoutedEvent = UIElement.MouseDownEvent
                       };
                       (e.OriginalSource as UIElement).RaiseEvent(args);
                   }),
                   System.Windows.Threading.DispatcherPriority.Input);
            } else {
                e.Handled = true;
            }
        }

        private void Count_ValueChanged(object sender, RoutedPropertyChangedEventArgs<object> e) {
            if (_currentSegment != null)
            {
                _currentSegment.Count = ((IntegerUpDown)sender).Value ?? 1;
                SetCurrentSegmentDirty(true);
            }
        }

        private void OpenBtn_Click(object sender, RoutedEventArgs e) {
            Debug.WriteLine("OpenBtn_Click");
            OpenFileDialog openFileDialog = new OpenFileDialog {
                Filter = "Video files|*.avi;*.divx;*.flv;*.mov;*.mp4;*.mpeg;*.mpg;*.qt;*.ram;*.rm;*.rmvb;*.wmv;*.webm|All files (*.*)|*.*"
            };
            if (openFileDialog.ShowDialog() != true) return;

            _currentSegment.Filepath = openFileDialog.FileName;
            FilenameLabel.Content = _currentSegment.Filename;
            _currentSegment.Start = _currentSegment.End = 0;
            _currentSegment.EndSet = false;
            _currentSegment.Count = 1;
            _currentSegment.Slow = _currentSegment.FastSeek = _currentSegment.Mute = null;

            SetCurrentSegmentDirty(true);

            LoadCurrentSegment(true);
        }

        private void ScrubberZoomIn_Click(object sender, RoutedEventArgs e) {
            //double centrePx = ScrubScroll.HorizontalOffset + ScrubScroll.ViewportWidth/2;
            //double centreProportion = centrePx / ScrubScroll.ExtentWidth;
            double thumbProportion = (double)_mediaPlayer.Time / _mediaPlayer.Length;

            double scrubrWidth = Math.Min(Scrubr.Width * 2, maxScrubrWidth);
            scrubrWidth = Math.Max(scrubrWidth, ScrubScroll.ViewportWidth);     // in case clip is very short
            Scrubr.Width  = scrubrWidth;
            ScrubScroll.ScrollToHorizontalOffset(thumbProportion * scrubrWidth - ScrubScroll.ViewportWidth/2);

            enableScrubberZoomButtons();
        }

        private void ScrubberZoomOut_Click(object sender, RoutedEventArgs e) {
            double centrePx = ScrubScroll.HorizontalOffset + ScrubScroll.ViewportWidth / 2;
            double centreProportion = centrePx / ScrubScroll.ExtentWidth;

            Scrubr.Width = Math.Max(Scrubr.Width / 2, ScrubScroll.ViewportWidth);
            ScrubScroll.ScrollToHorizontalOffset(centreProportion * Scrubr.Width - ScrubScroll.ViewportWidth / 2);

            enableScrubberZoomButtons();
        }

        private void enableScrubberZoomButtons()
        {
            bool loaded = _currentSegment != null && !_currentSegment.IsEmpty;
            ScrubberZoomIn.IsEnabled = loaded && Scrubr.Width < maxScrubrWidth;
            ScrubberZoomOut.IsEnabled = loaded && Scrubr.Width > ScrubScroll.ViewportWidth;
        }

        private void ScrubberZoomAll_Click(object sender, RoutedEventArgs e) {
            Scrubr.Width = ScrubScroll.ViewportWidth;
            enableScrubberZoomButtons();
        }

        private void SegmentListView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e) {
            Keyboard.Focus(SegmentListView);   // kludge to enable items in popup menu
            PreviewStop(true);
            CheckKeepSegmentChanges(true);
            // TODO 5 get popup menu to appear after MessageBox
        }

        private void SlowBox_Checked(object sender, RoutedEventArgs e) {
            if (_currentSegment != null)
            {
                _currentSegment.Slow = SlowBox.IsChecked;
                SetCurrentSegmentDirty(true);
            }
        }

        private void SeekBox_Checked(object sender, RoutedEventArgs e) {
            if (_currentSegment != null)
            {
                _currentSegment.FastSeek = SeekBox.IsChecked;
                SetCurrentSegmentDirty(true);
            }
        }

        private void MuteBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_currentSegment != null)
            {
                _currentSegment.Mute = MuteBox.IsChecked;
                SetCurrentSegmentDirty(true);
                LoadCurrentSegment(true);
            }
        }

        private void PlaylistDefault_Changed(object sender, RoutedEventArgs e) {
            SetPlaylistDirty(true);
        }

        private void PlaylistMute_Changed(object sender, RoutedEventArgs e)
        {
            PlaylistDefault_Changed(null, null);
            if (_currentSegment != null) LoadCurrentSegment(true);
        }

        private Boolean CheckKeepSegmentChanges(bool reload, bool alwaysKeep = false) {
            // If _currentSegment is dirty and user wants to keep changes, _currentSegment is copied to _selectedSegment.
            // reload: whether _selectedSegment should be reloaded if user chooses 'No' to abandon changes.
            // Returns true if okay to continue (ie, operation wasn't cancelled).
            if (!_currentSegmentDirty) return true;

            MessageBoxResult result = alwaysKeep? MessageBoxResult.Yes : 
                System.Windows.MessageBox.Show("Keep changes to this segment?", "Segment Changed", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes) {
                SaveCurrentSegment();
            } else if (result == MessageBoxResult.No && reload) { // revert to _selectedSegment; can happen if attempting to append
                LoadNewSegment(_selectedSegment);
            }

            return result != MessageBoxResult.Cancel;
        }

        private void SaveCurrentSegment()
        {
            _selectedSegment.Filepath = _currentSegment.Filepath;
            _selectedSegment.Start = _currentSegment.Start;
            _selectedSegment.End = _selectedSegment.IsEmpty? 0 : _currentSegment.End;
            _selectedSegment.EndSet = _selectedSegment.IsEmpty ? false : _currentSegment.EndSet;
            _selectedSegment.Count = _currentSegment.Count;
            _selectedSegment.Slow = _currentSegment.Slow;
            _selectedSegment.FastSeek = _currentSegment.FastSeek;
            _selectedSegment.Mute = _currentSegment.Mute;
            SetCurrentSegmentDirty(false);
            SetPlaylistDirty(true);
        }

        void SetCurrentSegmentDirty(bool dirty) {
            _currentSegmentDirty = dirty;
            SaveSegment.IsEnabled = UndoSegment.IsEnabled = dirty;
        }
        private void LoadCurrentSegment(bool startPaused)   // startPaused: if false, Play() until time changes to ensure new seg is displayed
        {
            videoView.Visibility =_currentSegment.IsEmpty? Visibility.Hidden : Visibility.Visible;
            Media media = LoadMedia();
            //_selectionStart = _currentSegment.Start;    // Scrubber will be updated in OnLengthChanged
            //_selectionEnd = _currentSegment.End;    // Scrubber will be updated in OnLengthChanged
            //Media m = new Media(_libVLC, _currentSegment.Filename, FromType.FromPath);
            //m.AddOption("rate=0.5");          // works, but makes scrubbing cumbersome
            /*if (preview)
            {
                if (_currentSegment.Start > 0) m.AddOption("start-time=" + _currentSegment.Start / 1000.0);   // works, but makes scrubbing cumbersome
                if (_currentSegment.End > 0) m.AddOption("stop-time=" + _currentSegment.End / 1000.0);   // works, but makes scrubbing cumbersome
                _mediaPlayer.Play(m);
            }*/
            //media.AddOption("file-caching=5000");    // probably doesn't do anything useful
            if (startPaused) media.AddOption("start-paused");        // can cause old seg to remain visible until new seg is played, esp if startTime requires long seek
            if (IsSegmentMute()) media.AddOption("no-audio");
            //_mediaPlayer.Media = media;
            pauseOnTimeChange = !startPaused;
            Debug.WriteLine("LoadCurrentSegment(): calling Play(media); start={0}; state={1}", _currentSegment.Start, _mediaPlayer.State);
            DumpThread("LoadCurrentSegment");
            bool playOk = _mediaPlayer.Play(media);           // can hang on segment changes, possibly because prev media is still playing or subject to callbacks
            //_mediaPlayer.SetRate(0.5f);       // works, but makes UI slow
            //_mediaPlayer.SetPause(true);
            Debug.WriteLine("LoadCurrentSegment(): setting .Time to start={0}", _currentSegment.Start);
            _mediaPlayer.Time = _currentSegment.Start;  // if .Start requires long seeking, media won't be cued automatically
            Debug.WriteLine("LoadCurrentSegment(): start={0}; Time={1}", _currentSegment.Start, _mediaPlayer.Time);
            DisplayTime(_currentSegment.Start);
            OpenBtn.IsEnabled = true;
        }

        private void DumpThread(string methodName)  // TODO 5 hide
        {
            Debug.WriteLine("{0}(): thread={1}={2}={3}; pool={4}", methodName, Environment.CurrentManagedThreadId, Thread.CurrentThread.ManagedThreadId, Thread.CurrentThread.GetHashCode(), 
                Thread.CurrentThread.IsThreadPoolThread);
        }

        private Media LoadMedia()
        {
            _selectionStart = _currentSegment.Start;    // Scrubber will be updated in OnLengthChanged
            _selectionEnd = _currentSegment.End;    // Scrubber will be updated in OnLengthChanged
            string filepath = "placeholder.png";
            if (!_currentSegment.IsEmpty) {
                if (File.Exists(_currentSegment.Filepath)) filepath = _currentSegment.Filepath;
                else System.Windows.MessageBox.Show("Can't find "+_currentSegment.Filename, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Media media = new Media(_libVLC, filepath, FromType.FromPath);
            //media.AddOption("rate=0.5");          // works, but makes UI slow to respond
            //media.StateChanged += OnStateChanged;
            return media;
        }

        /*private void OnStateChanged(object sender, MediaStateChangedEventArgs args) {
            Debug.WriteLine("OnStateChanged(): State={0}", args.State);
        }*/


        private void SetPlaylistDirty(Boolean dirty) {
            playlistDirty = dirty;
            String title;

            if (playlistFileName != null)
                title = Path.GetFileName(playlistFileName);
            else
                title = "[Untitled.m3u]";

            if (playlistDirty) title += " *";

            title += " - Video Playlist Maker";

            Title = title;
        }

        //********************************************************************************************** Menu Handlers *****
        private void New_CanExecute(Object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = true;
        }

        private void New_Executed(Object sender, ExecutedRoutedEventArgs e)
        {
            if (!CheckSavePlaylist()) return;   // if current playlist dirty, offer to save it

            ClearPlaylist();
            /*if (!CheckSavePlaylist()) return;   // if current playlist dirty, offer to save it

            // Reset state to initial:
            ClearCurrentSegment();
            segmentList.Clear();
            PlaylistOnTop.IsChecked = PlaylistLoop.IsChecked = PlaylistSlow.IsChecked = PlaylistFastSeek.IsChecked = PlaylistExit.IsChecked = false;
            //Debug.WriteLine("Before Stop: isPlaying={0}",_mediaPlayer.IsPlaying);
            if (_mediaPlayer.IsPlaying) _mediaPlayer.Stop();    // can freeze; might be asynchronous; perhaps shouldn't resume loading playlist until OnStopped(). Might be blocking if called on this thread.
            //Debug.WriteLine("After calling Stop");*/

            playlistFileName = null;
            SetPlaylistDirty(false);    // also updates window Title

            segmentList.Add(new Segment());
            _selectedSegment = segmentList[0];
            SegmentListView.SelectedIndex = 0;      // this causes automatic LoadNewSegment(_selectedSegment)
        }

        private void ClearPlaylist()
        // Doesn't initialise anything for subsequent playlist.
        {
            ClearCurrentSegment();
            segmentList.Clear();
            PlaylistOnTop.IsChecked = PlaylistLoop.IsChecked = PlaylistSlow.IsChecked = PlaylistFastSeek.IsChecked = PlaylistMute.IsChecked = PlaylistExit.IsChecked = false;
            //Debug.WriteLine("Before Stop: isPlaying={0}",_mediaPlayer.IsPlaying);
            if (_mediaPlayer.IsPlaying)
            {
                Debug.WriteLine("ClearPlaylist(): calling Stop()");
                _mediaPlayer.Stop();    // can freeze; might be asynchronous; perhaps shouldn't resume loading playlist until OnStopped(). Might be blocking if called on this thread.
            }
            //Debug.WriteLine("After calling Stop");
        }

        private void Copy_CanExecute(Object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = SegmentListView.SelectedIndex >= 0;
        }

        private void Copy_Executed(Object sender, ExecutedRoutedEventArgs e) { }    // never called; see SegmentListView_CopyingRowClipboardContent()

        private void Remainder_CanExecute(Object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = SegmentListView.SelectedIndex >= 0 && segmentList[SegmentListView.SelectedIndex].EndSet;
        }

        private void Remainder_Executed(Object sender, ExecutedRoutedEventArgs e) {
            AppendSegment(true);
        }

        private void SegmentListView_CopyingRowClipboardContent(object sender, DataGridRowClipboardEventArgs e)
        {
            AppendSegment(false);
        }

        private void AppendSegment(bool remainder)  // if remainder==true, set start of copy to end of source
        {
            int index = SegmentListView.SelectedIndex;
            Segment newSegment = new Segment(segmentList[index]);
            if (remainder)
            {
                newSegment.Start = newSegment.End;
                newSegment.End = 0;
                newSegment.EndSet = false;
            }
            segmentList.Insert(index + 1, newSegment);
            SetPlaylistDirty(true);
        }

        private void Insert_CanExecute(Object sender, CanExecuteRoutedEventArgs e) {
            e.CanExecute = true;
        }

        private void Insert_Executed(Object sender, ExecutedRoutedEventArgs e) {
            //Debug.WriteLine("Insert_Executed()");
            int index = SegmentListView.SelectedIndex;
            segmentList.Insert(index + 1, new Segment());
            SetPlaylistDirty(true);
        }

        private void Delete_CanExecute(Object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = SegmentListView.SelectedIndex >= 0;
        }

        private void Delete_Executed(Object sender, ExecutedRoutedEventArgs e)
        {
            //Debug.WriteLine("Delete_Executed()");
            ClearCurrentSegment();
            //string filepath = "placeholder.png";
            //Media media = new Media(_libVLC, filepath, FromType.FromPath);
            //_mediaPlayer.Play(media);           // TODO 0 can hang after deleting a seg from list
            segmentList.RemoveAt(SegmentListView.SelectedIndex);
            SetPlaylistDirty(true);
        }
        private void Open_CanExecute(Object sender, CanExecuteRoutedEventArgs e) {
            e.CanExecute = true;
        }

        private void Open_Executed(Object sender, ExecutedRoutedEventArgs e) {
            if (!CheckSavePlaylist()) return;   // if current playlist dirty, offer to save it

            ClearPlaylist();

            OpenFileDialog openFileDialog = new OpenFileDialog {
                Filter = "Playlists|*.m3u"
            };
            if (openFileDialog.ShowDialog() != true) return;

            ClearPlaylist();
            /*// Reset state to initial:
            ClearCurrentSegment();
            segmentList.Clear();
            PlaylistOnTop.IsChecked = PlaylistLoop.IsChecked = PlaylistSlow.IsChecked = PlaylistFastSeek.IsChecked = PlaylistExit.IsChecked = false;
            //Debug.WriteLine("Before Stop: isPlaying={0}",_mediaPlayer.IsPlaying);
            if (_mediaPlayer.IsPlaying)_mediaPlayer.Stop();    // can freeze; might be asynchronous; perhaps shouldn't resume loading playlist until OnStopped(). Might be blocking if called on this thread.
            //Debug.WriteLine("After calling Stop");*/

            Open(openFileDialog.FileName);

            /*// Read the file:
            string line, lineUpper;
            Segment segment = new Segment();
            playlistFileName = openFileDialog.FileName;
            bool playlistIsPLP = false;          // playlist file was created by this program, so allow saving to it
            using (StreamReader sr = new StreamReader(playlistFileName)) {
                while (sr.Peek() >= 0) {
                    line = sr.ReadLine();
                    lineUpper = line.ToUpper();
                    if (line == "" || lineUpper == "#EXTM3U") continue;
                    if (lineUpper.StartsWith("#EXTVLCOPT:")) AddPlaylistVLCOption(lineUpper, segment);
                    else if (lineUpper.StartsWith("#EXTPLPOPT:")) AddPlaylistPLPOption(lineUpper);
                    else if (lineUpper.StartsWith("#EXTPLP")) playlistIsPLP = true;
                    else if (lineUpper[0] == '#') continue;      // other unsupported options; eg, #EXTINF
                    else {
                        AddPlaylistFile(line, segment);
                        segment = new Segment();
                    }
                }
            }
            if (!playlistIsPLP) playlistFileName = null;

            SetPlaylistDirty(false);        // updates window Title

            if (segmentList.Count > 0) {
                // Cue up the first segment:
                _selectedSegment = segmentList[0];
                SegmentListView.SelectedIndex = 0;      // this causes automatic LoadNewSegment(_selectedSegment)
                //LoadNewSegment(_selectedSegment);     // done as a consequence of changing SegmentListView.SelectedIndex above
            }*/
        }

        private bool Open(string FileName)
        {
            // Read the file:
            string line, lineUpper;
            Segment segment = new Segment();
            bool playlistIsPLP = false;          // playlist file was created by this program, so allow saving to it
            try
            {
                using (StreamReader sr = new StreamReader(FileName))
                {
                    string playlistFolder = Path.GetDirectoryName(FileName);
                    while (sr.Peek() >= 0)
                    {
                        line = sr.ReadLine();
                        lineUpper = line.ToUpper();
                        if (line == "" || lineUpper == "#EXTM3U") continue;
                        if (lineUpper.StartsWith("#EXTVLCOPT:")) AddPlaylistVLCOption(lineUpper, segment);
                        else if (lineUpper.StartsWith("#EXTPLPOPT:")) AddPlaylistPLPOption(lineUpper);
                        else if (lineUpper.StartsWith("#EXTPLP")) playlistIsPLP = true;
                        else if (lineUpper[0] == '#') continue;      // other unsupported options; eg, #EXTINF
                        else
                        {
                            AddPlaylistFile(line, playlistFolder, segment);
                            segment = new Segment();
                        }
                    }
                }
            }
            catch(Exception) {
                System.Windows.MessageBox.Show("Can't open file", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false; 
            }

            playlistFileName = playlistIsPLP? FileName : null;

            SetPlaylistDirty(false);        // updates window Title

            if (segmentList.Count > 0)
            {
                // Cue up the first segment:
                _selectedSegment = segmentList[0];
                SegmentListView.SelectedIndex = 0;      // this causes automatic LoadNewSegment(_selectedSegment)
                //LoadNewSegment(_selectedSegment);     // done as a consequence of changing SegmentListView.SelectedIndex above
            }

            return true;
        }

        private void AddPlaylistVLCOption(string line, Segment segment) {
            line = line.Substring(11);

            //Debug.WriteLine("AddPlaylistVLCOption() {0}",line);
            if (line.StartsWith("START-TIME=")) segment.Start = (long)(1000 * Double.Parse(line.Substring(11)));
            else if (line.StartsWith("STOP-TIME=")) { segment.End = (long)(1000 * Double.Parse(line.Substring(10))); segment.EndSet = true; }
            else if (line.StartsWith("INPUT-REPEAT=")) segment.Count = int.Parse(line.Substring(13)) + 1;
            else if (line == "RATE=0.5") segment.Slow = true;
            else if (line == "RATE=1") segment.Slow = false;
            else if (line == "INPUT-FAST-SEEK") segment.FastSeek = true;
            else if (line == "NO-INPUT-FAST-SEEK") segment.FastSeek = false;
            else if (line == "NO-AUDIO") segment.Mute = true;
            else if (line == "AUDIO") segment.Mute = false;
            else if (line == "LOOP") PlaylistLoop.IsChecked = true;
            else if (line == "NO-LOOP") PlaylistLoop.IsChecked = false;
            else if (line == "VIDEO-ON-TOP") PlaylistOnTop.IsChecked = true;
            else if (line == "NO-VIDEO-ON-TOP") PlaylistOnTop.IsChecked = false;
            else if (line == "PLAY-AND-EXIT") PlaylistExit.IsChecked = true;
            else if (line == "NO-PLAY-AND-EXIT") PlaylistExit.IsChecked = false;
        }

        private void AddPlaylistPLPOption(string line) {
            line = line.Substring(11);

            if (line == "LOOP") PlaylistLoop.IsChecked = true;
            else if (line == "RATE=0.5") PlaylistSlow.IsChecked = true;
            else if (line == "INPUT-FAST-SEEK") PlaylistFastSeek.IsChecked = true;
            else if (line == "NO-AUDIO") PlaylistMute.IsChecked = true;
        }

        private void AddPlaylistFile(string line, string playlistFolder, Segment segment) {    // TODO 3.7 do something smart if it's a .m3u (PlaylistPlayer recurses into them)
            if (!line.Contains(":")) {  // no colon, so assume path is relative
                line = Path.Combine(playlistFolder, line);
                line = Path.GetFullPath(line);     // rationalises any ".." (parent directory) in line
            }
            segment.Filepath = line;
            segmentList.Add(segment);
            Debug.WriteLine("AddPlaylistFile(): {0}", line);
        }

        private void Save_CanExecute(Object sender, CanExecuteRoutedEventArgs e) {
            e.CanExecute = playlistFileName != null;
        }

        private void Save_Executed(Object sender, ExecutedRoutedEventArgs e) {
            // TODO 9 option to write .BAT file, calling VLC. Would steal focus.
            //Debug.WriteLine("Save()");
            if (!CheckKeepSegmentChanges(true, AUTO_KEEP_SEG_CHANGES_ON_SAVE)) return;

            if (playlistFileName == null) {
                Debug.WriteLine("Attempting to save file with no filename");
                return;
            }

            SavePlaylist();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!CheckSavePlaylist()) e.Cancel = true;
        }

        private bool CheckSavePlaylist()
        {
            // Returns true if playlist doesn't need saving, has been saved, or can be abandoned.
            // Returns false if playlist needs saving but user cancelled doing so.
            if (CheckKeepSegmentChanges(false) == false) return false;     // save seg if dirty

            if (playlistDirty)
            {
                MessageBoxResult result = System.Windows.MessageBox.Show("Playlist has unsaved changes. Save?", "Playlist Changed", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (result == MessageBoxResult.Cancel) return false;
                if (result == MessageBoxResult.Yes)
                {
                    if (playlistFileName == null)
                    {
                        if (SavePlaylistAs() == false) return false;            // don't exit if save cancelled
                    }
                    else Save_Executed(null, null);
                }
            }
            return true;
        }

        private void SavePlaylist() { 
            using (StreamWriter sw = new StreamWriter(playlistFileName)) {
                sw.WriteLine("#EXTM3U");
                sw.WriteLine("#EXTPLP");    // identifies this file as one created by us
                if (PlaylistOnTop.IsChecked == true) sw.WriteLine("#EXTVLCOPT:video-on-top");
                if (PlaylistLoop.IsChecked == true) sw.WriteLine("#EXTVLCOPT:loop");
                if (PlaylistSlow.IsChecked == true) sw.WriteLine("#EXTPLPOPT:rate=0.5");
                if (PlaylistFastSeek.IsChecked == true) sw.WriteLine("#EXTPLPOPT:input-fast-seek");
                if (PlaylistMute.IsChecked == true) sw.WriteLine("#EXTPLPOPT:no-audio");
                if (PlaylistExit.IsChecked == true) sw.WriteLine("#EXTVLCOPT:play-and-exit");
                sw.WriteLine("#EXTVLCOPT:fullscreen");      // doesn't work in VLC
                string playlistFolder = Path.GetDirectoryName(playlistFileName);
                foreach (Segment seg in segmentList) {
                    /*if (Scrubber.SelectionStart != 0)
                        sw.WriteLine("#EXTVLCOPT:start-time={0:f3}", Scrubber.SelectionStart / 1000.0);
                    if (Scrubber.SelectionEnd != Scrubber.Maximum)
                        sw.WriteLine("#EXTVLCOPT:stop-time={0:f3}", Scrubber.SelectionEnd / 1000.0);*/
                    //sw.WriteLine(_currentSegment.Filename);
                    if (seg.Start > 0) sw.WriteLine("#EXTVLCOPT:start-time={0:f3}", 0.001 * seg.Start);
                    if (seg.EndSet) sw.WriteLine("#EXTVLCOPT:stop-time={0:f3}", 0.001 * seg.End);
                    if (seg.Count > 1) sw.WriteLine("#EXTVLCOPT:input-repeat={0}", seg.Count - 1);
                    if (seg.Slow == true) sw.WriteLine("#EXTVLCOPT:rate=0.5"); else if (seg.Slow == false) sw.WriteLine("#EXTVLCOPT:rate=1");
                    if (seg.FastSeek == true) sw.WriteLine("#EXTVLCOPT:input-fast-seek"); else if (seg.FastSeek == false) sw.WriteLine("#EXTVLCOPT:no-input-fast-seek");
                    if (seg.Mute == true) sw.WriteLine("#EXTVLCOPT:no-audio"); else if (seg.Mute == false) sw.WriteLine("#EXTVLCOPT:audio");
                    string segPath = seg.Filepath;
                    if (RELATIVE_PATHS) segPath = MakeRelativePath(segPath, playlistFolder);
                    sw.WriteLine(segPath);
                }
            }
            SetPlaylistDirty(false);
        }

        static string MakeRelativePath(string absolutePath, string pivotFolder) // https://gist.github.com/AlexeyMz/183b3ab2c4dbb0a7de5b
        {
            //string folder = Path.IsPathRooted(pivotFolder)
            //    ? pivotFolder : Path.GetFullPath(pivotFolder);
            string folder = pivotFolder;
            Uri pathUri = new Uri(absolutePath);
            // Folders must end in a slash
            if (!folder.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                folder += Path.DirectorySeparatorChar;
            }
            Uri folderUri = new Uri(folder);
            Uri relativeUri = folderUri.MakeRelativeUri(pathUri);
            return Uri.UnescapeDataString(
                relativeUri.ToString().Replace('/', Path.DirectorySeparatorChar));
        }

        private void SegmentListView_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;

            DataObject data = new DataObject();
            data.SetData("SegmentListRow", SegmentListView.SelectedIndex);
            DragDrop.DoDragDrop(SegmentListView, data, DragDropEffects.Move);
        }

        private void SegmentListView_DragOver(object sender, DragEventArgs e)
        {
            Point dropPosition = e.GetPosition(SegmentListView);
            System.Windows.Media.HitTestResult hitTestResult = System.Windows.Media.VisualTreeHelper.HitTest(SegmentListView, dropPosition);
            DataGridRow dataGridRowUnderMouse = GetParentOfType<DataGridRow>(hitTestResult.VisualHit);
            if (dataGridRowUnderMouse == null)
            {
                e.Effects = DragDropEffects.None;
            } else {
                int dropIndex = dataGridRowUnderMouse.GetIndex();
                //Segment sourceSeg = (Segment)e.Data.GetData("Segment");
                int dragIndex = (int)e.Data.GetData("SegmentListRow");
                e.Effects = dragIndex == dropIndex? DragDropEffects.None : DragDropEffects.Move;
            }

            e.Handled = true;
        }

        private void SegmentListView_Drop(object sender, DragEventArgs e)
        {
            Point dropPosition = e.GetPosition(SegmentListView);
            System.Windows.Media.HitTestResult hitTestResult = System.Windows.Media.VisualTreeHelper.HitTest(SegmentListView, dropPosition);
            DataGridRow dataGridRowUnderMouse = GetParentOfType<DataGridRow>(hitTestResult.VisualHit);
            if (dataGridRowUnderMouse == null) return;
            int dragIndex = (int)e.Data.GetData("SegmentListRow");
            int dropIndex = dataGridRowUnderMouse.GetIndex();
            //ListBoxItem dropSegment = ((ListBoxItem)(sender)).DataContext;

            Segment dragSegment = segmentList[dragIndex];
            ClearCurrentSegment();
            segmentList.RemoveAt(dragIndex);
            segmentList.Insert(dropIndex, dragSegment);
            SetPlaylistDirty(true);
        }

        private T GetParentOfType<T>(DependencyObject element) where T : DependencyObject   // https://stackoverflow.com/questions/28959392/dragdrop-to-datagrid-item-control-get-current-item-index
        {
            Type type = typeof(T);
            if (element == null) return null;
            DependencyObject parent = System.Windows.Media.VisualTreeHelper.GetParent(element);
            if (parent == null && ((FrameworkElement)element).Parent is DependencyObject)
                parent = ((FrameworkElement)element).Parent;
            if (parent == null) return null;
            else if (parent.GetType() == type || parent.GetType().IsSubclassOf(type))
                return parent as T;
            return GetParentOfType<T>(parent);
        }

        private void SaveSegment_Click(object sender, RoutedEventArgs e)
        {
            SaveCurrentSegment();
        }

        private void UndoSegment_Click(object sender, RoutedEventArgs e)
        {
            LoadNewSegment(_selectedSegment);
        }

        private void TrimEndBtn_Click(object sender, RoutedEventArgs e)
        {
            SetSelectionEnd(Math.Max(Scrubr.SelectionEnd - GetFrameDuration(), 0));
            SetCurrentSegmentDirty(true);

            if (isPreviewingEnd)
            {
                PreviewStop(false);
                PreviewEndBtn_Click(null, null);    // might cause hang; wait for onStopped()?
            }
        }

        private void ExtendEndBtn_Click(object sender, RoutedEventArgs e)
        {
            double maxEnd = _mediaPlayer.Length;
            double newEnd = Scrubr.SelectionEnd + GetFrameDuration();
            if (newEnd >= maxEnd) newEnd = 0;   // sentinel for EOF
            SetSelectionEnd(newEnd);
            SetCurrentSegmentDirty(true);

            if (isPreviewingEnd)
            {
                PreviewStop(false);
                PreviewEndBtn_Click(null, null);    // might cause hang; wait for onStopped()?
            }
        }

        private void PlayCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (PauseBtn.IsEnabled) PauseBtn_Click(null, null);
        }

        private void NextFrameCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (FrameBtn.IsEnabled) FrameBtn_Click(null, null);
        }

        private void BackFrameCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (BackBtn.IsEnabled) BackBtn_Click(null, null);
        }

        private void TrimEndCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (TrimEndBtn.IsEnabled) TrimEndBtn_Click(null, null);
        }
        private void ExtendEndCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (ExtendEndBtn.IsEnabled) ExtendEndBtn_Click(null, null);
        }
        private void StartCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (StartBtn.IsEnabled) StartBtn_Click(null, null);
        }
        private void EndCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (EndBtn.IsEnabled) EndBtn_Click(null, null);
        }
        private void PreviewStartCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (PreviewStartBtn.IsEnabled) PreviewStartBtn_Click(null, null);
        }

        private void PreviewEndCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (PreviewEndBtn.IsEnabled) PreviewEndBtn_Click(null, null);
        }

        private void GoToStartCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (GoToStartBtn.IsEnabled) GoToStartBtn_Click(null, null);
        }

        private void GoToEndCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (GoToEndBtn.IsEnabled) GoToEndBtn_Click(null, null);
        }

        private void ScrubScroll_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double ViewportWidth = ScrubScroll.ViewportWidth;
            if (ViewportWidth == 0) return;
            ViewportWidth += e.NewSize.Width - e.PreviousSize.Width;
            Scrubr.Width = ViewportWidth;
        }

        private void SaveAs_CanExecute(Object sender, CanExecuteRoutedEventArgs e) {
            e.CanExecute = true;
        }

        private void SaveAs_Executed(Object sender, ExecutedRoutedEventArgs e) {
            SavePlaylistAs();
        }

        private bool SavePlaylistAs()
        {
            // Returns false if save was cancelled.
            if (!CheckKeepSegmentChanges(true, AUTO_KEEP_SEG_CHANGES_ON_SAVE)) return false;

            SaveFileDialog saveFileDialog = new SaveFileDialog
            {
                Filter = "Playlists|*.m3u"
            };
            if (saveFileDialog.ShowDialog() != true) return false;

            playlistFileName = saveFileDialog.FileName;
            SavePlaylist();
            return true;
        }

        private void FileExitCommand_Executed(object sender, ExecutedRoutedEventArgs e) {
            Close();
        }
    }
}
// TODO 9 See if frame rate can be determined (if _mediaPlayer.Fps==0; Josephine.m3u) from stepping forward by 1 frame. Hard because NextFrame() doesn't always go forward by same amount.
// TODO 3.4 button to crop one frame from start - check whether this would be useful. Could also add one frame at start.
// TODO 5 current seg clear (new) btn (akin to save and revert)
// TODO 3.7 Playlist menu: Settings(?)
// TODO 5 Dispose() all libvlc objects when done with them: https://github.com/videolan/libvlcsharp/blob/3.x/docs/best_practices.md#dispose-of-libvlc-objects-when-done
// TODO 5 scale and aspect ratio (also in PlaylistPlayer)
// TODO 9 thumbnails in SegmentListView: https://github.com/videolan/libvlcsharp/blob/3.x/docs/how_do_I_do_X.md#how-do-i-take-a-snapshot-of-the-video, https://github.com/videolan/libvlcsharp/blob/3.x/docs/how_do_I_do_X.md#how-do-i-get-individual-frames-out-of-a-video, https://github.com/mfkl/libvlcsharp-samples/tree/master/PreviewThumbnailExtractor