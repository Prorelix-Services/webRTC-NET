// Recording + automatic backend transcription.
// Recording = browser MediaRecorder -> WebM chunks -> server.
// Transcription = browser microphone -> short WebM clip -> browser converts to 16 kHz WAV
// -> server -> Whisper.net -> SQLite + JSON + TXT.

(function () {
  const $ = (id) => document.getElementById(id);
  const toast = (m) => {
    const t = $('toast');
    if (!t) return;
    t.textContent = m;
    t.classList.add('show');
    setTimeout(() => t.classList.remove('show'), 2600);
  };

  const myName = () => localStorage.getItem('meet_name') || 'Guest';

  let status = { recording: false, recordingId: null, transcription: false };
  let recorder = null;
  let transcriber = null;
  let captionTimer = null;
  let attached = null;
  let starting = false;
  let lastNote = 0;

  const noteOnce = (m) => {
    if (Date.now() - lastNote > 20000) {
      lastNote = Date.now();
      toast(m);
    }
  };

  // ---------------- signaling ----------------

  function send(obj) {
    if (typeof ws !== 'undefined' && ws && ws.readyState === 1) {
      ws.send(JSON.stringify(obj));
      return true;
    }
    return false;
  }

  function attach(socket) {
    if (!socket || attached === socket) return;
    attached = socket;

    socket.addEventListener('message', (ev) => {
      let msg;
      try { msg = JSON.parse(ev.data); } catch { return; }

      if (msg.type === 'status') applyStatus(msg);
      if (msg.type === 'transcript') addTranscript(msg.segment);
    });

    const ask = () => {
      if (socket.readyState === 1)
        socket.send(JSON.stringify({ type: 'get-status' }));
      else
        socket.addEventListener('open', ask, { once: true });
    };
    ask();
  }

  window.onSignalingReady = attach;

  setInterval(() => {
    try {
      if (typeof ws !== 'undefined' && ws && ws !== attached) attach(ws);
    } catch (_) {}
  }, 300);

  window.beforeLeave = async () => {
    stopTranscriber();
    if (recorder) await stopRecording();
  };

  function applyStatus(s) {
    const previous = status;
    status = s;

    $('recPill').style.display = s.recording ? '' : 'none';
    $('ccPill').style.display = s.transcription ? '' : 'none';
    $('recBtn').classList.toggle('rec-on', s.recording);
    $('ccBtn').classList.toggle('on', s.transcription);

    if (s.recording !== previous.recording && !recorder)
      toast(s.recording ? 'Recording started' : 'Recording stopped');

    if (s.transcription !== previous.transcription)
      toast(s.transcription ? 'Live captions on' : 'Live captions off');

    // IMPORTANT:
    // Recording automatically turns transcription on for every participant.
    // This is independent from the CC button.
    if (s.recording && s.recordingId && !transcriber)
      startTranscriber(s.recordingId);
    else if (!s.recording && s.transcription && !transcriber)
      startTranscriber(null);

    if (!s.recording && !s.transcription && transcriber)
      stopTranscriber();

    // If another participant stopped the meeting recording, the owner finishes its recorder.
    if (!s.recording && recorder)
      stopRecording();
  }

  // ---------------- recording ----------------

  $('recBtn').addEventListener('click', async () => {
    if (starting) return;

    if (recorder || status.recording) {
      send({ type: 'set-recording', active: false, recordingId: status.recordingId || '' });
      return;
    }

    if (typeof MediaRecorder === 'undefined') {
      toast('Recording is not supported in this browser');
      return;
    }

    starting = true;

    try {
      const mime = pickVideoMime();
      if (!mime) throw new Error('This browser cannot create WebM recordings');

      const response = await fetch(
        `/api/recordings?room=${encodeURIComponent(roomId)}&ext=webm`,
        { method: 'POST' }
      );

      if (!response.ok)
        throw new Error((await response.json().catch(() => ({}))).detail || 'server refused');

      const { id } = await response.json();

      recorder = new CompositeRecorder(id, mime);
      await recorder.start();

      if (!send({ type: 'set-recording', active: true, recordingId: id })) {
        await stopRecording();
        throw new Error('Not connected to the meeting server');
      }

      // Start immediately; all participants will receive the same recordingId.
      startTranscriber(id);
    } catch (e) {
      recorder = null;
      toast('Could not start recording: ' + e.message);
    } finally {
      starting = false;
    }
  });

  function pickVideoMime() {
    return [
      'video/webm;codecs=vp9,opus',
      'video/webm;codecs=vp8,opus',
      'video/webm'
    ].find((x) => MediaRecorder.isTypeSupported(x)) || '';
  }

  async function stopRecording() {
    const current = recorder;
    recorder = null;
    if (!current) return;

    await current.stop();
    toast('Recording saved');
    loadRecordings();
  }

  class CompositeRecorder {
    constructor(id, mime) {
      this.id = id;
      this.mime = mime;
      this.startedAt = Date.now();
      this.queue = Promise.resolve();
      this.sources = new Map();
      this.worker = null;
      this.ctx = null;
      this.rec = null;
      this.canvasStream = null;
    }

    async start() {
      const canvas = document.createElement('canvas');
      canvas.width = 1280;
      canvas.height = 720;

      const ctx = canvas.getContext('2d');
      this.ctx = new (window.AudioContext || window.webkitAudioContext)();
      this.dest = this.ctx.createMediaStreamDestination();

      const draw = () => {
        const videos = [...document.querySelectorAll('#videoGrid video')]
          .filter(v => v.srcObject);

        const seen = new Set();

        for (const video of videos) {
          const stream = video.srcObject;
          seen.add(stream.id);

          if (!this.sources.has(stream.id) && stream.getAudioTracks().length) {
            try {
              const src = this.ctx.createMediaStreamSource(
                new MediaStream(stream.getAudioTracks())
              );
              src.connect(this.dest);
              this.sources.set(stream.id, src);
            } catch (_) {}
          }
        }

        for (const key of this.sources.keys()) {
          if (!seen.has(key)) {
            try { this.sources.get(key).disconnect(); } catch (_) {}
            this.sources.delete(key);
          }
        }

        ctx.fillStyle = '#10141a';
        ctx.fillRect(0, 0, canvas.width, canvas.height);

        const n = Math.max(videos.length, 1);
        const cols = Math.ceil(Math.sqrt(n));
        const rows = Math.ceil(n / cols);
        const w = canvas.width / cols;
        const h = canvas.height / rows;

        videos.forEach((video, i) => {
          if (!video.videoWidth) return;

          const scale = Math.min(
            (w - 8) / video.videoWidth,
            (h - 8) / video.videoHeight
          );

          const dw = video.videoWidth * scale;
          const dh = video.videoHeight * scale;

          ctx.drawImage(
            video,
            (i % cols) * w + (w - dw) / 2,
            Math.floor(i / cols) * h + (h - dh) / 2,
            dw,
            dh
          );
        });
      };

      draw();

      // Keeps canvas updates running when a browser throttles timers.
      this.worker = new Worker(
        URL.createObjectURL(
          new Blob(['setInterval(() => postMessage(0), 66)'],
          { type: 'text/javascript' })
        )
      );
      this.worker.onmessage = draw;

      this.canvasStream = canvas.captureStream(15);
      const tracks = [
        ...this.dest.stream.getAudioTracks(),
        ...this.canvasStream.getVideoTracks()
      ];

      this.rec = new MediaRecorder(
        new MediaStream(tracks),
        { mimeType: this.mime, videoBitsPerSecond: 2_000_000 }
      );

      this.rec.ondataavailable = (e) => {
        if (e.data && e.data.size)
          this.queue = this.queue.then(() => this.upload(e.data));
      };

      // Smaller chunks = less data lost if the network/browser dies.
      this.rec.start(2000);
    }

    async upload(blob) {
      for (let attempt = 0; attempt < 5; attempt++) {
        try {
          const response = await fetch(
            `/api/recordings/${this.id}/chunks`,
            { method: 'PUT', body: blob }
          );

          if (response.ok) return;
        } catch (_) {}

        await new Promise(r => setTimeout(r, 500 * (attempt + 1)));
      }

      console.warn('Recording chunk upload failed');
    }

    async stop() {
      if (this.worker) this.worker.terminate();

      if (this.rec && this.rec.state !== 'inactive') {
        await new Promise(resolve => {
          this.rec.addEventListener('stop', resolve, { once: true });
          this.rec.stop();
        });
      }

      await this.queue;

      try {
        await fetch(`/api/recordings/${this.id}/complete`, {
          method: 'POST',
          keepalive: true
        });
      } catch (_) {}

      try { if (this.ctx) await this.ctx.close(); } catch (_) {}
    }
  }

  // ---------------- automatic transcription ----------------

  $('ccBtn').addEventListener('click', () => {
    send({ type: 'set-transcription', active: !status.transcription });
  });

  function startTranscriber(recordingId) {
    if (transcriber || typeof localStream === 'undefined' || !localStream) return;
    if (!localStream.getAudioTracks().length) return;

    const audioTrack = localStream.getAudioTracks()[0];
    transcriber = {
      running: true,
      recordingId: recordingId || null,
      startedAt: Date.now(),
      track: audioTrack
    };

    runTranscriptionLoop(transcriber).catch(e => {
      console.warn('Transcription loop stopped:', e);
    });
  }

  function stopTranscriber() {
    if (transcriber) transcriber.running = false;
    transcriber = null;
  }

  async function runTranscriptionLoop(state) {
    const CLIP_MS = 8000;
    const audio = new MediaStream([state.track]);
    const mime = ['audio/webm;codecs=opus', 'audio/webm']
      .find(x => MediaRecorder.isTypeSupported(x));

    if (!mime) {
      noteOnce('This browser cannot create audio clips for transcription');
      return;
    }

    while (state.running && transcriber === state) {
      const clipStart = Date.now();

      const blob = await captureAudioClip(audio, mime, CLIP_MS);
      if (!state.running || transcriber !== state) break;

      if (blob && blob.size > 1000 && state.track.enabled) {
        try {
          const wav = await webmToWav(blob);

          const params = new URLSearchParams({
            room: roomId,
            name: myName(),
            start: ((clipStart - state.startedAt) / 1000).toString()
          });

          if (state.recordingId)
            params.set('recordingId', state.recordingId);

          const response = await fetch(`/api/transcribe?${params}`, {
            method: 'POST',
            headers: { 'Content-Type': 'audio/wav' },
            body: wav
          });

          const result = await response.json().catch(() => ({}));

          if (result.error)
            noteOnce('Transcription error: ' + result.error);
        } catch (e) {
          noteOnce('Transcription clip failed');
          console.warn(e);
        }
      }
    }
  }

  function captureAudioClip(stream, mime, ms) {
    return new Promise((resolve) => {
      const recorder = new MediaRecorder(stream, { mimeType: mime });
      const chunks = [];

      recorder.ondataavailable = e => {
        if (e.data && e.data.size) chunks.push(e.data);
      };

      recorder.onstop = () =>
        resolve(new Blob(chunks, { type: recorder.mimeType }));

      recorder.onerror = () => resolve(null);

      recorder.start();

      setTimeout(() => {
        if (recorder.state !== 'inactive') recorder.stop();
      }, ms);
    });
  }

  // Browser decodes its own WebM/Opus, then creates standard PCM WAV.
  // Whisper.net can read WAV directly, so FFmpeg is no longer needed.
  async function webmToWav(blob) {
    const AudioCtx = window.AudioContext || window.webkitAudioContext;
    const sourceContext = new AudioCtx();

    try {
      const buffer = await sourceContext.decodeAudioData(
        await blob.arrayBuffer()
      );

      const targetRate = 16000;
      const frames = Math.ceil(buffer.duration * targetRate);
      const offline = new OfflineAudioContext(1, frames, targetRate);
      const source = offline.createBufferSource();
      source.buffer = buffer;
      source.connect(offline.destination);
      source.start();

      const rendered = await offline.startRendering();
      const samples = rendered.getChannelData(0);

      return encodeWav(samples, targetRate);
    } finally {
      try { await sourceContext.close(); } catch (_) {}
    }
  }

  function encodeWav(samples, sampleRate) {
    const buffer = new ArrayBuffer(44 + samples.length * 2);
    const view = new DataView(buffer);

    writeString(view, 0, 'RIFF');
    view.setUint32(4, 36 + samples.length * 2, true);
    writeString(view, 8, 'WAVE');
    writeString(view, 12, 'fmt ');
    view.setUint32(16, 16, true);
    view.setUint16(20, 1, true);       // PCM
    view.setUint16(22, 1, true);       // mono
    view.setUint32(24, sampleRate, true);
    view.setUint32(28, sampleRate * 2, true);
    view.setUint16(32, 2, true);
    view.setUint16(34, 16, true);
    writeString(view, 36, 'data');
    view.setUint32(40, samples.length * 2, true);

    let offset = 44;
    for (let i = 0; i < samples.length; i++) {
      const sample = Math.max(-1, Math.min(1, samples[i]));
      view.setInt16(
        offset,
        sample < 0 ? sample * 0x8000 : sample * 0x7fff,
        true
      );
      offset += 2;
    }

    return new Blob([view], { type: 'audio/wav' });
  }

  function writeString(view, offset, text) {
    for (let i = 0; i < text.length; i++)
      view.setUint8(offset + i, text.charCodeAt(i));
  }

  // ---------------- transcript UI ----------------

  function addTranscript(seg) {
    const cap = $('caption');
    cap.innerHTML = '';

    const name = document.createElement('b');
    name.textContent = seg.name + ': ';
    cap.append(name, document.createTextNode(seg.text));
    cap.style.display = '';

    clearTimeout(captionTimer);
    captionTimer = setTimeout(() => cap.style.display = 'none', 7000);

    const log = $('trLog');
    if (log.querySelector('.empty-note')) log.innerHTML = '';

    const line = document.createElement('div');
    line.className = 'tr-line';

    const speaker = document.createElement('b');
    speaker.textContent = seg.name + ': ';
    line.append(speaker, document.createTextNode(seg.text));

    log.appendChild(line);
    log.scrollTop = log.scrollHeight;
  }

  // ---------------- recordings panel ----------------

  $('panelBtn').addEventListener('click', () => {
    const panel = $('sidePanel');
    panel.style.display = panel.style.display === 'none' ? '' : 'none';
    if (panel.style.display === '') loadRecordings();
  });

  $('panelClose').addEventListener('click', () =>
    $('sidePanel').style.display = 'none'
  );

  $('trDownload').href =
    `/api/rooms/${encodeURIComponent(roomId)}/transcript?format=text&download=1`;

  async function loadRecordings() {
    const box = $('recList');

    try {
      const response = await fetch(
        `/api/rooms/${encodeURIComponent(roomId)}/recordings`
      );
      const items = await response.json();

      box.innerHTML = '';

      if (!items.length) {
        box.innerHTML = '<span class="empty-note">None yet</span>';
        return;
      }

      for (const item of items) {
        const row = document.createElement('div');
        const mb = (item.size / 1048576).toFixed(1);

        if (item.status === 'ready') {
          const link = document.createElement('a');
          link.href = item.url;
          link.textContent = `${item.created.slice(11)} - ${mb} MB - download`;
          row.appendChild(link);
        } else {
          row.textContent =
            `${item.created.slice(11)} - ${item.status}...`;
        }

        box.appendChild(row);
      }
    } catch (_) {}
  }

  // Refresh after returning to a tab/network.
  window.addEventListener('online', loadRecordings);
})();
