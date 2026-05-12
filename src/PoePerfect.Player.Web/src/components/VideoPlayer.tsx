import Hls from "hls.js";
import { ArrowLeft, Captions, Maximize, Pause, Play, Volume2 } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import type { Channel } from "../domain";

type VideoPlayerProps = {
  channel: Channel;
  onClose: () => void;
};

type TrackOption = {
  id: number;
  label: string;
};

type NativeAudioTrackList = {
  length: number;
  [index: number]: {
    id?: string;
    label?: string;
    language?: string;
    enabled: boolean;
  };
};

export function VideoPlayer({ channel, onClose }: VideoPlayerProps) {
  const videoRef = useRef<HTMLVideoElement | null>(null);
  const hlsRef = useRef<Hls | null>(null);
  const [isPlaying, setIsPlaying] = useState(false);
  const [currentTime, setCurrentTime] = useState(0);
  const [duration, setDuration] = useState(0);
  const [audioTracks, setAudioTracks] = useState<TrackOption[]>([]);
  const [subtitleTracks, setSubtitleTracks] = useState<TrackOption[]>([]);
  const [selectedAudioTrack, setSelectedAudioTrack] = useState(-1);
  const [selectedSubtitleTrack, setSelectedSubtitleTrack] = useState(-1);

  useEffect(() => {
    const video = videoRef.current;
    if (!video) {
      return;
    }

    const player = video;
    let hls: Hls | undefined;
    const canPlayNativeHls = player.canPlayType("application/vnd.apple.mpegurl");

    function refreshNativeTracks() {
      const nativeAudioTracks = (player as HTMLVideoElement & { audioTracks?: NativeAudioTrackList }).audioTracks;
      if (nativeAudioTracks?.length) {
        setAudioTracks(
          Array.from({ length: nativeAudioTracks.length }, (_, index) => ({
            id: index,
            label: nativeAudioTracks[index].label || nativeAudioTracks[index].language || `Ljud ${index + 1}`,
          })),
        );
        const enabledIndex = Array.from({ length: nativeAudioTracks.length })
          .findIndex((_, index) => nativeAudioTracks[index].enabled);
        setSelectedAudioTrack(enabledIndex >= 0 ? enabledIndex : 0);
      }

      const nativeSubtitleTracks = Array.from(player.textTracks ?? []);
      if (nativeSubtitleTracks.length > 0) {
        setSubtitleTracks(
          nativeSubtitleTracks.map((track, index) => ({
            id: index,
            label: track.label || track.language || `Undertext ${index + 1}`,
          })),
        );
        const showingIndex = nativeSubtitleTracks.findIndex((track) => track.mode === "showing");
        setSelectedSubtitleTrack(showingIndex);
      }
    }

    if (channel.url.includes(".m3u8") && Hls.isSupported() && !canPlayNativeHls) {
      hls = new Hls({
        lowLatencyMode: true,
        backBufferLength: 30,
        renderTextTracksNatively: true,
      });
      hlsRef.current = hls;
      hls.loadSource(channel.url);
      hls.attachMedia(video);
      hls.on(Hls.Events.AUDIO_TRACKS_UPDATED, (_, data) => {
        setAudioTracks(
          data.audioTracks.map((track, index) => ({
            id: index,
            label: track.name || track.lang || `Ljud ${index + 1}`,
          })),
        );
        setSelectedAudioTrack(hls?.audioTrack ?? -1);
      });
      hls.on(Hls.Events.SUBTITLE_TRACKS_UPDATED, (_, data) => {
        setSubtitleTracks(
          data.subtitleTracks.map((track, index) => ({
            id: index,
            label: track.name || track.lang || `Undertext ${index + 1}`,
          })),
        );
        setSelectedSubtitleTrack(hls?.subtitleTrack ?? -1);
      });
      hls.on(Hls.Events.MANIFEST_PARSED, () => {
        refreshNativeTracks();
        void video.play().catch(() => {});
      });
    } else {
      hlsRef.current = null;
      video.src = channel.url;
      video.addEventListener("loadedmetadata", refreshNativeTracks);
      void video.play().catch(() => {});
    }

    const updatePlaying = () => setIsPlaying(!video.paused);
    const updateTime = () => {
      setCurrentTime(video.currentTime || 0);
      setDuration(Number.isFinite(video.duration) ? video.duration : 0);
    };

    video.addEventListener("play", updatePlaying);
    video.addEventListener("pause", updatePlaying);
    video.addEventListener("timeupdate", updateTime);
    video.addEventListener("durationchange", updateTime);

    return () => {
      hls?.destroy();
      hlsRef.current = null;
      video.removeEventListener("play", updatePlaying);
      video.removeEventListener("pause", updatePlaying);
      video.removeEventListener("timeupdate", updateTime);
      video.removeEventListener("durationchange", updateTime);
      video.removeAttribute("src");
      video.load();
    };
  }, [channel.url]);

  function togglePlayback() {
    const video = videoRef.current;
    if (!video) {
      return;
    }

    if (video.paused) {
      void video.play();
    } else {
      video.pause();
    }
  }

  function seek(value: number) {
    const video = videoRef.current;
    if (!video || !Number.isFinite(value)) {
      return;
    }

    video.currentTime = value;
    setCurrentTime(value);
  }

  function selectAudioTrack(value: number) {
    setSelectedAudioTrack(value);
    const hls = hlsRef.current;
    if (hls) {
      hls.audioTrack = value;
      return;
    }

    const video = videoRef.current;
    const nativeAudioTracks = (video as HTMLVideoElement & { audioTracks?: NativeAudioTrackList } | null)?.audioTracks;
    if (!nativeAudioTracks) {
      return;
    }

    for (let index = 0; index < nativeAudioTracks.length; index += 1) {
      nativeAudioTracks[index].enabled = index === value;
    }
  }

  function selectSubtitleTrack(value: number) {
    setSelectedSubtitleTrack(value);
    const hls = hlsRef.current;
    if (hls) {
      hls.subtitleDisplay = value >= 0;
      hls.subtitleTrack = value;
      return;
    }

    const video = videoRef.current;
    if (!video) {
      return;
    }

    Array.from(video.textTracks ?? []).forEach((track, index) => {
      track.mode = index === value ? "showing" : "disabled";
    });
  }

  function toggleFullscreen() {
    const host = videoRef.current?.parentElement;
    if (!host) {
      return;
    }

    if (document.fullscreenElement) {
      void document.exitFullscreen();
    } else {
      void host.requestFullscreen();
    }
  }

  const canSeek = duration > 0;

  return (
    <div className="player-screen">
      <div className="player-topbar">
        <button className="icon-text-button" onClick={onClose} autoFocus>
          <ArrowLeft size={20} />
          Tillbaka
        </button>
        <div>
          <h1>{channel.name}</h1>
          <p>{channel.categoryName}</p>
        </div>
      </div>

      <div className="video-stage">
        <video ref={videoRef} className="video-player" playsInline />
        <div className="player-controls">
          <button className="round-control-button" onClick={togglePlayback} aria-label={isPlaying ? "Pausa" : "Spela"}>
            {isPlaying ? <Pause size={22} fill="currentColor" /> : <Play size={22} fill="currentColor" />}
          </button>

          <div className="time-stack">
            <input
              type="range"
              min={0}
              max={Math.max(0, duration)}
              step={0.25}
              value={Math.min(currentTime, duration || currentTime)}
              disabled={!canSeek}
              onChange={(event) => seek(Number(event.target.value))}
            />
            <span>{formatTime(currentTime)} / {canSeek ? formatTime(duration) : "--:--"}</span>
          </div>

          <label className="track-select-label">
            <Volume2 size={17} />
            <select
              value={selectedAudioTrack}
              disabled={audioTracks.length === 0}
              onChange={(event) => selectAudioTrack(Number(event.target.value))}
            >
              {audioTracks.length === 0 ? <option value={-1}>Standardljud</option> : null}
              {audioTracks.map((track) => (
                <option value={track.id} key={track.id}>{track.label}</option>
              ))}
            </select>
          </label>

          <label className="track-select-label">
            <Captions size={17} />
            <select
              value={selectedSubtitleTrack}
              disabled={subtitleTracks.length === 0}
              onChange={(event) => selectSubtitleTrack(Number(event.target.value))}
            >
              <option value={-1}>Undertexter av</option>
              {subtitleTracks.map((track) => (
                <option value={track.id} key={track.id}>{track.label}</option>
              ))}
            </select>
          </label>

          <button className="round-control-button" onClick={toggleFullscreen} aria-label="Fullscreen">
            <Maximize size={20} />
          </button>
        </div>
      </div>
    </div>
  );
}

function formatTime(seconds: number) {
  if (!Number.isFinite(seconds) || seconds <= 0) {
    return "00:00";
  }

  const totalSeconds = Math.floor(seconds);
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const remainingSeconds = totalSeconds % 60;
  return hours > 0
    ? `${hours}:${minutes.toString().padStart(2, "0")}:${remainingSeconds.toString().padStart(2, "0")}`
    : `${minutes.toString().padStart(2, "0")}:${remainingSeconds.toString().padStart(2, "0")}`;
}
