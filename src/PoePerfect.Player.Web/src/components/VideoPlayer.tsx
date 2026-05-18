import Hls from "hls.js";
import { ArrowLeft, Captions, Maximize, Minus, Pause, Play, Plus, Volume2 } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import type { KeyboardEvent, ReactNode } from "react";
import { LoadingSpinner } from "./LoadingSpinner";
import type { Channel } from "../domain";
import { startGatewayStream, stopGatewayStream } from "../services/gateway";
import { fetchText } from "../services/http";
import type { StreamFormatPreference, VodPlayerMode } from "../services/storage";

type VideoPlayerProps = {
  channel: Channel;
  playerMode?: VodPlayerMode;
  streamFormat?: StreamFormatPreference;
  showTrackDiagnostics?: boolean;
  onClose: () => void;
  onProgress?: (positionSeconds: number, durationSeconds: number) => void;
  onGatewaySessionChange?: (gateway: {
    sessionId: string;
    playlistUrl: string;
    startAtSeconds: number;
    audioTrackIndex: number;
    subtitleTrackIndex: number;
  }) => void;
};

type TrackOption = {
  value: string;
  source: "hls" | "native" | "external" | "gateway" | "gateway-subtitle";
  id: number;
  label: string;
  language?: string;
  url?: string;
};

type MediaTrackProbeResponse = {
  audioTracks?: Array<{
    audioTrackIndex?: number;
    label?: string;
    language?: string;
  }>;
};

type TrackPickerKind = "audio" | "subtitle";

type SubtitleCue = {
  startTime: number;
  endTime: number;
  text: string;
};

type NativeAudioTrackList = EventTarget & {
  length: number;
  [index: number]: {
    id?: string;
    label?: string;
    language?: string;
    enabled: boolean;
  };
};

type MutableTextTrackCue = TextTrackCue & {
  startTime: number;
  endTime: number;
};

type OriginalTextTrackCueTime = {
  startTime: number;
  endTime: number;
};

type PositionedTextTrackCue = TextTrackCue & {
  line: number | "auto";
};

const noAudioTrack = "";
const subtitlesOff = "off";
const uiAutoHideMs = 2500;
const maxSubtitleOffsetSeconds = 30;
const subtitleLineDefault = -2;
const subtitleLineWithControlsVisible = -6;
const progressReportIntervalMs = 5000;

export function VideoPlayer({
  channel,
  playerMode = "smart",
  streamFormat = "default",
  showTrackDiagnostics = false,
  onClose,
  onProgress,
  onGatewaySessionChange,
}: VideoPlayerProps) {
  const videoRef = useRef<HTMLVideoElement | null>(null);
  const hlsRef = useRef<Hls | null>(null);
  const activeGatewaySessionRef = useRef(channel.gatewaySessionId);
  const activeChannelUrlRef = useRef(channel.url);
  const gatewayOffsetRef = useRef(channel.gatewayStartOffsetSeconds ?? 0);
  const gatewayAudioTrackIndexRef = useRef(0);
  const gatewaySubtitleTrackIndexRef = useRef(channel.gatewaySubtitleTrackIndex ?? -1);
  const isUsingGatewayAudioTracksRef = useRef(false);
  const selectedSubtitleTrackRef = useRef(subtitlesOff);
  const shouldRestoreGatewaySubtitleRef = useRef(false);
  const subtitleTrackToRestoreRef = useRef(subtitlesOff);
  const activeSubtitleLoadAbortRef = useRef<AbortController | null>(null);
  const loadedExternalSubtitlesRef = useRef(new Map<string, SubtitleCue[]>());
  const activeOverlaySubtitleCuesRef = useRef<SubtitleCue[]>([]);
  const overlaySubtitleFrameRef = useRef<number | undefined>();
  const overlaySubtitleIndexRef = useRef(-1);
  const overlaySubtitleTextRef = useRef("");
  const originalSubtitleCueTimesRef = useRef(new WeakMap<TextTrackCue, OriginalTextTrackCueTime>());
  const originalSubtitleCueLinesRef = useRef(new WeakMap<TextTrackCue, PositionedTextTrackCue["line"]>());
  const subtitleOffsetRef = useRef(0);
  const uiHideTimeoutRef = useRef<number | undefined>();
  const isPointerOverPlayerChromeRef = useRef(false);
  const shouldAutoPlayRef = useRef(true);
  const lastProgressReportAtRef = useRef(0);
  const hasAppliedResumePositionRef = useRef(false);
  const hasRetriedOriginalSourceRef = useRef(false);
  const onProgressRef = useRef(onProgress);
  const [activeSourceUrl, setActiveSourceUrl] = useState(channel.url);
  const [gatewayOffset, setGatewayOffset] = useState(channel.gatewayStartOffsetSeconds ?? 0);
  const [isPlaying, setIsPlaying] = useState(false);
  const [currentTime, setCurrentTime] = useState(0);
  const [duration, setDuration] = useState(0);
  const [knownTimelineDuration, setKnownTimelineDuration] = useState(channel.durationSeconds ?? 0);
  const [audioTracks, setAudioTracks] = useState<TrackOption[]>([]);
  const [mediaSubtitleTracks, setMediaSubtitleTracks] = useState<TrackOption[]>([]);
  const [externalSubtitleTracks, setExternalSubtitleTracks] = useState<TrackOption[]>([]);
  const [selectedAudioTrack, setSelectedAudioTrack] = useState(noAudioTrack);
  const [selectedSubtitleTrack, setSelectedSubtitleTrack] = useState(subtitlesOff);
  const [overlaySubtitleText, setOverlaySubtitleText] = useState("");
  const [subtitleProbeMessage, setSubtitleProbeMessage] = useState("");
  const [scrubTime, setScrubTime] = useState<number | undefined>();
  const [subtitleOffsetSeconds, setSubtitleOffsetSeconds] = useState(0);
  const [isUiVisible, setIsUiVisible] = useState(true);
  const [isBuffering, setIsBuffering] = useState(true);
  const [trackPicker, setTrackPicker] = useState<TrackPickerKind | undefined>();
  const [trackDiagnostics, setTrackDiagnostics] = useState("");
  const isGatewayPlayback = Boolean(channel.gatewaySessionId && channel.originalUrl);

  useEffect(() => {
    onProgressRef.current = onProgress;
  }, [onProgress]);

  useEffect(() => {
    const nextOffset = channel.gatewayStartOffsetSeconds ?? 0;
    activeGatewaySessionRef.current = channel.gatewaySessionId;
    gatewayOffsetRef.current = nextOffset;
    gatewaySubtitleTrackIndexRef.current = channel.gatewaySubtitleTrackIndex ?? gatewaySubtitleTrackIndexRef.current;
    setGatewayOffset(nextOffset);
    setActiveSourceUrl(channel.url);
    setScrubTime(undefined);
    setKnownTimelineDuration(channel.durationSeconds ?? 0);
    setIsBuffering(true);
    shouldAutoPlayRef.current = true;
    hasAppliedResumePositionRef.current = false;
    hasRetriedOriginalSourceRef.current = false;
  }, [channel.durationSeconds, channel.gatewaySessionId, channel.gatewayStartOffsetSeconds, channel.gatewaySubtitleTrackIndex, channel.url]);

  useEffect(() => {
    selectedSubtitleTrackRef.current = selectedSubtitleTrack;
    const parsedTrack = parseTrackValue(selectedSubtitleTrack);
    if (parsedTrack?.source !== "external") {
      activeOverlaySubtitleCuesRef.current = [];
      overlaySubtitleIndexRef.current = -1;
      stopOverlaySubtitleLoop();
      setOverlaySubtitleTextIfChanged("");
    }
  }, [selectedSubtitleTrack]);

  useEffect(() => {
    subtitleOffsetRef.current = subtitleOffsetSeconds;
    applyCurrentSubtitleOffset();
  }, [activeSourceUrl, subtitleOffsetSeconds]);

  useEffect(() => {
    applySubtitleControlLift();
  }, [activeSourceUrl, isUiVisible, selectedSubtitleTrack]);

  useEffect(() => {
    revealPlayerUi();
    return () => {
      if (uiHideTimeoutRef.current) {
        window.clearTimeout(uiHideTimeoutRef.current);
      }
      stopOverlaySubtitleLoop();
    };
  }, [activeSourceUrl]);

  useEffect(() => {
    if (!trackPicker) {
      return;
    }

    setIsUiVisible(true);
    if (uiHideTimeoutRef.current) {
      window.clearTimeout(uiHideTimeoutRef.current);
      uiHideTimeoutRef.current = undefined;
    }
  }, [trackPicker]);

  useEffect(() => {
    const video = videoRef.current;
    if (!video) {
      return;
    }

    const player = video;
    let hls: Hls | undefined;
    const canPlayNativeHls = player.canPlayType("application/vnd.apple.mpegurl");
    let isDisposed = false;

    player.autoplay = true;
    player.playsInline = true;
    player.preload = "auto";
    activeChannelUrlRef.current = activeSourceUrl;
    const shouldKeepSubtitleChoice = shouldRestoreGatewaySubtitleRef.current
      && selectedSubtitleTrackRef.current !== subtitlesOff;
    const shouldKeepGatewayTrackChoices = isGatewayPlayback;
    isUsingGatewayAudioTracksRef.current = false;
    clearLoadedExternalSubtitles();
    originalSubtitleCueTimesRef.current = new WeakMap<TextTrackCue, OriginalTextTrackCueTime>();
    originalSubtitleCueLinesRef.current = new WeakMap<TextTrackCue, PositionedTextTrackCue["line"]>();
    if (!shouldKeepGatewayTrackChoices) {
      setAudioTracks([]);
      setSelectedAudioTrack(noAudioTrack);
    }
    if (!shouldKeepSubtitleChoice && !shouldKeepGatewayTrackChoices) {
      setMediaSubtitleTracks([]);
      setExternalSubtitleTracks([]);
    }
    if (!shouldKeepSubtitleChoice && !shouldKeepGatewayTrackChoices) {
      setSelectedSubtitleTrack(subtitlesOff);
    }
    setSubtitleProbeMessage("");
    setIsBuffering(true);

    function refreshNativeAudioTracks() {
      const nativeAudioTracks = (player as HTMLVideoElement & { audioTracks?: NativeAudioTrackList }).audioTracks;
      if (nativeAudioTracks?.length) {
        const tracks = Array.from({ length: nativeAudioTracks.length }, (_, index) => ({
          value: makeTrackValue("native", index),
          source: "native" as const,
          id: index,
          label: nativeAudioTracks[index].label || nativeAudioTracks[index].language || `Ljud ${index + 1}`,
        }));

        setAudioTracks(
          tracks,
        );
        const enabledIndex = Array.from({ length: nativeAudioTracks.length })
          .findIndex((_, index) => nativeAudioTracks[index].enabled);
        setSelectedAudioTrack(makeTrackValue("native", enabledIndex >= 0 ? enabledIndex : 0));
      } else if (!hls) {
        setAudioTracks([]);
        setSelectedAudioTrack(noAudioTrack);
      }
    }

    function refreshNativeSubtitleTracks() {
      if (hls?.subtitleTracks.length) {
        return;
      }

      const nativeSubtitleTracks = getSelectableTextTracks(player);
      if (nativeSubtitleTracks.length > 0) {
        setSubtitleProbeMessage("");
      }
      setMediaSubtitleTracks(
        nativeSubtitleTracks.map((track, index) => ({
          value: makeTrackValue("native", index),
          source: "native",
          id: index,
          label: track.label || track.language || `Undertext ${index + 1}`,
        })),
      );

      const showingIndex = nativeSubtitleTracks.findIndex((track) => track.mode === "showing");
      setSelectedSubtitleTrack(showingIndex >= 0 ? makeTrackValue("native", showingIndex) : subtitlesOff);
    }

    function refreshNativeTracks() {
      refreshNativeAudioTracks();
      refreshNativeSubtitleTracks();
    }

    async function loadExternalSubtitleTracks() {
      const externalTracks = dedupeSubtitleTracks(channel.subtitleTracks ?? []);
      if (externalTracks.length === 0) {
        return;
      }

      setExternalSubtitleTracks(
        externalTracks.map((track, index) => {
          const gatewaySubtitleTrackIndex = isGatewayPlayback ? getGatewaySubtitleTrackIndex(track) : undefined;
          if (gatewaySubtitleTrackIndex !== undefined) {
            return {
              value: makeTrackValue("gateway-subtitle", gatewaySubtitleTrackIndex),
              source: "gateway-subtitle" as const,
              id: gatewaySubtitleTrackIndex,
              label: track.label,
              language: track.language,
            };
          }

          return {
            value: makeTrackValue("external", index),
            source: "external" as const,
            id: index,
            label: track.label,
            language: track.language,
            url: track.url,
          };
        }),
      );
    }

    function reportVideoError() {
      const error = player.error;
      logPlayerEvent("video.error", {
        code: error?.code,
        message: error?.message,
        networkState: player.networkState,
        readyState: player.readyState,
      });
      if (fallbackToOriginalSource("video_error")) {
        return;
      }
      if (error) {
        setSubtitleProbeMessage(`Video-fel: ${error.message || `kod ${error.code}`}`);
      }
      setIsBuffering(false);
    }

    function reportVideoWaiting() {
      if (!player.paused || shouldAutoPlayRef.current) {
        setIsBuffering(true);
      }
      logPlayerEvent("video.waiting", {
        currentTime: player.currentTime,
        networkState: player.networkState,
        readyState: player.readyState,
      });
    }

    function reportVideoPlaying() {
      setIsBuffering(false);
      applyCurrentSubtitleOffset();
      logPlayerEvent("video.playing", {
        currentTime: player.currentTime,
        readyState: player.readyState,
      });
      if (isGatewayPlayback) {
        setSubtitleProbeMessage("");
      }
    }

    function reportVideoStalled() {
      if (!player.paused || shouldAutoPlayRef.current) {
        setIsBuffering(true);
      }
      logPlayerEvent("video.stalled", {
        currentTime: player.currentTime,
        networkState: player.networkState,
        readyState: player.readyState,
      });
    }

    function ensurePlayback(reason: string) {
      if (isDisposed || !shouldAutoPlayRef.current || !player.paused) {
        return;
      }

      const playPromise = player.play();
      if (playPromise) {
        void playPromise.catch((error: unknown) => {
          logPlayerEvent("video.play_failed", {
            reason,
            name: error instanceof Error ? error.name : typeof error,
            message: error instanceof Error ? error.message : String(error),
            readyState: player.readyState,
            networkState: player.networkState,
          });
          if (isGatewayPlayback && error instanceof DOMException && error.name === "NotAllowedError") {
            setSubtitleProbeMessage("Tryck Spela for att starta.");
          }
          setIsBuffering(false);
        });
      }
    }

    const shouldUseHlsJs = playerMode === "smart" && activeSourceUrl.includes(".m3u8") && Hls.isSupported();
    if (shouldUseHlsJs) {
      logPlayerEvent("hls.start", {
        url: describeMediaUrl(activeSourceUrl),
        nativeHls: canPlayNativeHls || "",
        playerMode,
        streamFormat,
      });
      hls = new Hls({
        lowLatencyMode: false,
        backBufferLength: 45,
        maxBufferLength: 45,
        maxMaxBufferLength: 90,
        maxBufferHole: 0.7,
        maxBufferSize: 120 * 1000 * 1000,
        startFragPrefetch: true,
        renderTextTracksNatively: true,
      });
      hlsRef.current = hls;
      hls.loadSource(activeSourceUrl);
      hls.attachMedia(video);
      hls.on(Hls.Events.MEDIA_ATTACHED, () => {
        logPlayerEvent("hls.media_attached");
      });
      hls.on(Hls.Events.MANIFEST_LOADING, (_, data) => {
        setIsBuffering(true);
        logHlsEvent("hls.manifest_loading", data);
      });
      hls.on(Hls.Events.MANIFEST_LOADED, (_, data) => {
        logHlsEvent("hls.manifest_loaded", data);
      });
      hls.on(Hls.Events.LEVEL_LOADED, (_, data) => {
        logHlsEvent("hls.level_loaded", data);
      });
      hls.on(Hls.Events.FRAG_LOADING, (_, data) => {
        logHlsEvent("hls.frag_loading", data);
      });
      hls.on(Hls.Events.FRAG_LOADED, (_, data) => {
        logHlsEvent("hls.frag_loaded", data);
      });
      hls.on(Hls.Events.FRAG_PARSED, (_, data) => {
        logHlsEvent("hls.frag_parsed", data);
        ensurePlayback("frag_parsed");
      });
      hls.on(Hls.Events.AUDIO_TRACKS_UPDATED, (_, data) => {
        if (isGatewayPlayback && isUsingGatewayAudioTracksRef.current) {
          return;
        }

        const tracks = data.audioTracks.map((track, index) => ({
          value: makeTrackValue("hls", index),
          source: "hls" as const,
          id: index,
          label: track.name || track.lang || `Ljud ${index + 1}`,
        }));
        setAudioTracks(tracks);
        setSelectedAudioTrack(tracks[hls?.audioTrack ?? 0]?.value ?? tracks[0]?.value ?? noAudioTrack);
        updateTrackDiagnostics("hls_audio_tracks", hls);
      });
      hls.on(Hls.Events.AUDIO_TRACK_SWITCHED, () => {
        if (isGatewayPlayback && isUsingGatewayAudioTracksRef.current) {
          return;
        }

        setSelectedAudioTrack(makeTrackValue("hls", hls?.audioTrack ?? 0));
      });
      hls.on(Hls.Events.SUBTITLE_TRACKS_UPDATED, (_, data) => {
        if (isGatewayPlayback) {
          if (gatewaySubtitleTrackIndexRef.current >= 0 && data.subtitleTracks.length > 0) {
            hls!.subtitleDisplay = true;
            hls!.subtitleTrack = 0;
            syncNativeSubtitleModes(player, 0);
            applyCurrentSubtitleOffset();
            setSubtitleProbeMessage("");
          }
          shouldRestoreGatewaySubtitleRef.current = false;
          subtitleTrackToRestoreRef.current = subtitlesOff;
          return;
        }

        const tracks = data.subtitleTracks.map((track, index) => ({
          value: makeTrackValue("hls", index),
          source: "hls" as const,
          id: index,
          label: track.name || track.lang || `Undertext ${index + 1}`,
        }));
        setMediaSubtitleTracks(tracks);
        updateTrackDiagnostics("hls_subtitle_tracks", hls);
        const shouldRestoreGatewaySubtitle = isGatewayPlayback && shouldRestoreGatewaySubtitleRef.current && tracks.length > 0;
        const restoreTrack = parseTrackValue(subtitleTrackToRestoreRef.current);
        const restoredTrackId = restoreTrack?.source === "hls" && tracks[restoreTrack.id]
          ? restoreTrack.id
          : 0;
        hls!.subtitleDisplay = shouldRestoreGatewaySubtitle;
        hls!.subtitleTrack = shouldRestoreGatewaySubtitle ? restoredTrackId : -1;
        setSelectedSubtitleTrack(shouldRestoreGatewaySubtitle ? tracks[restoredTrackId].value : subtitlesOff);
        applyCurrentSubtitleOffset();
        if (shouldRestoreGatewaySubtitle) {
          shouldRestoreGatewaySubtitleRef.current = false;
          subtitleTrackToRestoreRef.current = subtitlesOff;
        }
      });
      hls.on(Hls.Events.SUBTITLE_TRACKS_CLEARED, () => {
        if (isGatewayPlayback) {
          setMediaSubtitleTracks([]);
          return;
        }

        setMediaSubtitleTracks([]);
        if (!shouldRestoreGatewaySubtitleRef.current) {
          setSelectedSubtitleTrack(subtitlesOff);
        }
      });
      hls.on(Hls.Events.SUBTITLE_TRACK_SWITCH, (_, data) => {
        if (isGatewayPlayback && gatewaySubtitleTrackIndexRef.current >= 0) {
          syncNativeSubtitleModes(player, data.id);
          applyCurrentSubtitleOffset();
          return;
        }

        setSelectedSubtitleTrack(data.id >= 0 ? makeTrackValue("hls", data.id) : subtitlesOff);
        syncNativeSubtitleModes(player, data.id);
        applyCurrentSubtitleOffset();
      });
      hls.on(Hls.Events.NON_NATIVE_TEXT_TRACKS_FOUND, () => {
        applyCurrentSubtitleOffset();
        refreshNativeSubtitleTracks();
      });
      hls.on(Hls.Events.CUES_PARSED, () => {
        applyCurrentSubtitleOffset();
        refreshNativeSubtitleTracks();
      });
      hls.on(Hls.Events.ERROR, (_, data) => {
        const message = `HLS ${data.type}: ${data.details}`;
        console.warn("[player]", message, data);
        logHlsEvent("hls.error", data);
        if (data.fatal) {
          if (fallbackToOriginalSource("hls_error")) {
            return;
          }
          setSubtitleProbeMessage(`Gateway/player-fel: ${data.details}`);
          setIsBuffering(false);
        }
      });
      hls.on(Hls.Events.MANIFEST_PARSED, () => {
        logPlayerEvent("hls.manifest_parsed", {
          audioTracks: hls?.audioTracks.length ?? 0,
          subtitleTracks: hls?.subtitleTracks.length ?? 0,
        });
        refreshNativeTracks();
        updateTrackDiagnostics("hls_manifest", hls);
        applyCurrentSubtitleOffset();
        ensurePlayback("manifest_parsed");
      });
    } else {
      hlsRef.current = null;
      video.src = activeSourceUrl;
      logPlayerEvent("native.start", {
        url: describeMediaUrl(activeSourceUrl),
        playerMode,
        streamFormat,
        nativeHls: canPlayNativeHls || "",
      });
      video.addEventListener("loadedmetadata", refreshNativeTracks);
      ensurePlayback("direct_source");
    }

    const nativeAudioTracks = (player as HTMLVideoElement & { audioTracks?: NativeAudioTrackList }).audioTracks;
    nativeAudioTracks?.addEventListener("addtrack", refreshNativeAudioTracks);
    nativeAudioTracks?.addEventListener("change", refreshNativeAudioTracks);
    const refreshNativeSubtitleTracksAndOffset = () => {
      refreshNativeSubtitleTracks();
      applyCurrentSubtitleOffset();
    };
    player.textTracks?.addEventListener("addtrack", refreshNativeSubtitleTracksAndOffset);
    player.textTracks?.addEventListener("change", refreshNativeSubtitleTracksAndOffset);
    void loadExternalSubtitleTracks();
    if (isGatewayPlayback && channel.originalUrl) {
      void loadGatewayAudioTracks(channel.originalUrl);
    }

    const updatePlaying = () => {
      setIsPlaying(!video.paused);
      if (video.paused) {
        setIsBuffering(false);
      }
      updateTrackDiagnostics("play_state", hls);
    };
    const updateTime = () => {
      setCurrentTime(video.currentTime || 0);
      const nextDuration = Number.isFinite(video.duration) ? video.duration : 0;
      setDuration(nextDuration);
      const observedTimelineDuration = isGatewayPlayback
        ? gatewayOffsetRef.current + nextDuration
        : nextDuration;
      if (observedTimelineDuration > 0) {
        setKnownTimelineDuration((current) => Math.max(current, observedTimelineDuration, channel.durationSeconds ?? 0));
      }
      reportWatchProgress(video.currentTime || 0, observedTimelineDuration);
    };
    const showBuffering = () => {
      if (!video.paused || shouldAutoPlayRef.current) {
        setIsBuffering(true);
      }
    };
    const hideBufferingAndApplySubtitles = () => {
      setIsBuffering(false);
      applyCurrentSubtitleOffset();
      updateTrackDiagnostics("ready", hls);
    };
    const ensurePlaybackFromLoadedData = () => ensurePlayback("loadeddata");
    const ensurePlaybackFromCanPlay = () => ensurePlayback("canplay");
    const applyResumeFromMetadata = () => applyInitialResumePosition();

    video.addEventListener("loadstart", showBuffering);
    video.addEventListener("play", updatePlaying);
    video.addEventListener("pause", updatePlaying);
    video.addEventListener("timeupdate", updateTime);
    video.addEventListener("durationchange", updateTime);
    video.addEventListener("loadedmetadata", applyResumeFromMetadata);
    video.addEventListener("loadeddata", hideBufferingAndApplySubtitles);
    video.addEventListener("canplay", hideBufferingAndApplySubtitles);
    video.addEventListener("seeked", hideBufferingAndApplySubtitles);
    video.addEventListener("loadeddata", ensurePlaybackFromLoadedData);
    video.addEventListener("canplay", ensurePlaybackFromCanPlay);
    video.addEventListener("seeking", showBuffering);
    video.addEventListener("error", reportVideoError);
    video.addEventListener("waiting", reportVideoWaiting);
    video.addEventListener("playing", reportVideoPlaying);
    video.addEventListener("stalled", reportVideoStalled);

    return () => {
      isDisposed = true;
      reportWatchProgress(video.currentTime || 0, isGatewayPlayback
        ? gatewayOffsetRef.current + (Number.isFinite(video.duration) ? video.duration : 0)
        : Number.isFinite(video.duration) ? video.duration : knownTimelineDuration,
      true);
      hls?.destroy();
      hlsRef.current = null;
      cancelActiveSubtitleLoad();
      clearLoadedExternalSubtitles();
      video.removeEventListener("play", updatePlaying);
      video.removeEventListener("pause", updatePlaying);
      video.removeEventListener("timeupdate", updateTime);
      video.removeEventListener("durationchange", updateTime);
      video.removeEventListener("loadedmetadata", applyResumeFromMetadata);
      video.removeEventListener("loadstart", showBuffering);
      video.removeEventListener("loadeddata", hideBufferingAndApplySubtitles);
      video.removeEventListener("canplay", hideBufferingAndApplySubtitles);
      video.removeEventListener("seeked", hideBufferingAndApplySubtitles);
      video.removeEventListener("loadeddata", ensurePlaybackFromLoadedData);
      video.removeEventListener("canplay", ensurePlaybackFromCanPlay);
      video.removeEventListener("seeking", showBuffering);
      video.removeEventListener("error", reportVideoError);
      video.removeEventListener("waiting", reportVideoWaiting);
      video.removeEventListener("playing", reportVideoPlaying);
      video.removeEventListener("stalled", reportVideoStalled);
      nativeAudioTracks?.removeEventListener("addtrack", refreshNativeAudioTracks);
      nativeAudioTracks?.removeEventListener("change", refreshNativeAudioTracks);
      video.textTracks?.removeEventListener("addtrack", refreshNativeSubtitleTracksAndOffset);
      video.textTracks?.removeEventListener("change", refreshNativeSubtitleTracksAndOffset);
      video.removeAttribute("src");
      video.load();
    };
  }, [activeSourceUrl, channel.contentType, channel.durationSeconds, channel.resumePositionSeconds, channel.subtitleTracks, isGatewayPlayback, playerMode, showTrackDiagnostics, streamFormat]);

  function reportWatchProgress(
    localPositionSeconds = videoRef.current?.currentTime || 0,
    durationSeconds = knownTimelineDuration,
    force = false,
  ) {
    const progressHandler = onProgressRef.current;
    if (!progressHandler || channel.contentType === "live") {
      return;
    }

    const now = Date.now();
    if (!force && now - lastProgressReportAtRef.current < progressReportIntervalMs) {
      return;
    }

    lastProgressReportAtRef.current = now;
    const absolutePosition = isGatewayPlayback
      ? gatewayOffsetRef.current + localPositionSeconds
      : localPositionSeconds;
    progressHandler(absolutePosition, durationSeconds || channel.durationSeconds || 0);
  }

  function updateTrackDiagnostics(reason: string, hls?: Hls | null) {
    if (!showTrackDiagnostics) {
      return;
    }

    const video = videoRef.current as (HTMLVideoElement & { audioTracks?: NativeAudioTrackList }) | null;
    const nativeAudioCount = video?.audioTracks?.length ?? 0;
    const nativeSubtitleCount = video ? getSelectableTextTracks(video).length : 0;
    const hlsAudioCount = hls?.audioTracks.length ?? hlsRef.current?.audioTracks.length ?? 0;
    const hlsSubtitleCount = hls?.subtitleTracks.length ?? hlsRef.current?.subtitleTracks.length ?? 0;
    const sourceKind = getStreamKind(activeSourceUrl);
    const nextDiagnostics = [
      playerMode.toUpperCase(),
      streamFormat.toUpperCase(),
      sourceKind.toUpperCase(),
      `A ${nativeAudioCount}/${hlsAudioCount}`,
      `S ${nativeSubtitleCount}/${hlsSubtitleCount}`,
      reason,
    ].join(" | ");
    setTrackDiagnostics(nextDiagnostics);
    logPlayerEvent("tracks.diagnostics", {
      reason,
      playerMode,
      streamFormat,
      sourceKind,
      nativeAudioCount,
      nativeSubtitleCount,
      hlsAudioCount,
      hlsSubtitleCount,
      readyState: video?.readyState,
      networkState: video?.networkState,
    });
  }

  function fallbackToOriginalSource(reason: string) {
    const originalUrl = channel.originalUrl;
    if (
      !originalUrl
      || isGatewayPlayback
      || activeSourceUrl === originalUrl
      || hasRetriedOriginalSourceRef.current
    ) {
      return false;
    }

    hasRetriedOriginalSourceRef.current = true;
    shouldAutoPlayRef.current = true;
    setIsBuffering(true);
    setSubtitleProbeMessage("Formatet fungerade inte, provar originalström...");
    logPlayerEvent("source.fallback_original", {
      reason,
      from: describeMediaUrl(activeSourceUrl),
      to: describeMediaUrl(originalUrl),
    });
    setActiveSourceUrl(originalUrl);
    return true;
  }

  function applyInitialResumePosition() {
    const video = videoRef.current;
    const resumePosition = channel.resumePositionSeconds ?? 0;
    if (!video || isGatewayPlayback || hasAppliedResumePositionRef.current || resumePosition <= 0) {
      return;
    }

    const durationLimit = getTimelineDurationLimit(video.duration, channel.durationSeconds, knownTimelineDuration);
    if (durationLimit > 0 && resumePosition >= durationLimit - 20) {
      hasAppliedResumePositionRef.current = true;
      return;
    }

    hasAppliedResumePositionRef.current = true;
    video.currentTime = resumePosition;
  }

  function closePlayer() {
    reportWatchProgress(videoRef.current?.currentTime || 0, knownTimelineDuration, true);
    onClose();
  }

  function togglePlayback() {
    const video = videoRef.current;
    if (!video) {
      return;
    }

    if (video.paused) {
      shouldAutoPlayRef.current = true;
      void video.play();
    } else {
      shouldAutoPlayRef.current = false;
      setIsBuffering(false);
      video.pause();
    }
  }

  function seek(value: number) {
    if (isGatewayPlayback) {
      void restartGatewayAt(value);
      return;
    }

    const video = videoRef.current;
    if (!video || !Number.isFinite(value)) {
      return;
    }

    video.currentTime = value;
    setCurrentTime(value);
  }

  function updateScrub(value: number) {
    if (!Number.isFinite(value)) {
      return;
    }

    if (isGatewayPlayback) {
      setScrubTime(value);
      return;
    }

    seek(value);
  }

  function commitScrub() {
    if (scrubTime === undefined) {
      return;
    }

    const nextTime = scrubTime;
    setScrubTime(undefined);
    seek(nextTime);
  }

  async function restartGatewayAt(value: number) {
    const sourceUrl = channel.originalUrl;
    if (!sourceUrl) {
      return;
    }

    const targetTime = clamp(value, 0, getTimelineDurationLimit(channel.durationSeconds, knownTimelineDuration, value));
    const subtitleTrackToRestore = selectedSubtitleTrackRef.current;
    shouldRestoreGatewaySubtitleRef.current = subtitleTrackToRestore !== subtitlesOff;
    subtitleTrackToRestoreRef.current = subtitleTrackToRestore;
    shouldAutoPlayRef.current = true;
    setIsBuffering(true);
    setSubtitleProbeMessage(`Hoppar till ${formatTime(targetTime)}...`);

    try {
      const previousSessionId = activeGatewaySessionRef.current;
      hlsRef.current?.destroy();
      hlsRef.current = null;
      if (videoRef.current) {
        videoRef.current.pause();
        videoRef.current.removeAttribute("src");
        videoRef.current.load();
      }

      if (previousSessionId) {
        await stopGatewayStream(previousSessionId);
        await delay(600);
      }

      const gateway = await startGatewayStream(
        sourceUrl,
        channel.gatewaySubtitleLanguage ?? "swe",
        targetTime,
        gatewayAudioTrackIndexRef.current,
        gatewaySubtitleTrackIndexRef.current,
      );
      activeGatewaySessionRef.current = gateway.sessionId;
      const nextOffset = gateway.startAtSeconds ?? targetTime;
      gatewayOffsetRef.current = nextOffset;
      setGatewayOffset(nextOffset);
      setCurrentTime(0);
      setDuration(0);
      onGatewaySessionChange?.({
        sessionId: gateway.sessionId,
        playlistUrl: gateway.playlistUrl,
        startAtSeconds: nextOffset,
        audioTrackIndex: gatewayAudioTrackIndexRef.current,
        subtitleTrackIndex: gatewaySubtitleTrackIndexRef.current,
      });
      setActiveSourceUrl(gateway.playlistUrl);
    } catch (error) {
      setSubtitleProbeMessage(error instanceof Error
        ? `Kunde inte hoppa: ${error.message}`
        : "Kunde inte hoppa i filmen.");
    }
  }

  function selectAudioTrack(value: string) {
    setSelectedAudioTrack(value);
    const parsedTrack = parseTrackValue(value);
    if (!parsedTrack) {
      return;
    }

    if (parsedTrack.source === "gateway") {
      gatewayAudioTrackIndexRef.current = parsedTrack.id;
      if (isGatewayPlayback) {
        const targetTime = gatewayOffsetRef.current + (videoRef.current?.currentTime || 0);
        void restartGatewayAt(targetTime);
      }
      return;
    }

    const hls = hlsRef.current;
    if (parsedTrack.source === "hls" && hls) {
      hls.audioTrack = parsedTrack.id;
      return;
    }

    const video = videoRef.current;
    const nativeAudioTracks = (video as HTMLVideoElement & { audioTracks?: NativeAudioTrackList } | null)?.audioTracks;
    if (!nativeAudioTracks || parsedTrack.source !== "native") {
      return;
    }

    for (let index = 0; index < nativeAudioTracks.length; index += 1) {
      nativeAudioTracks[index].enabled = index === parsedTrack.id;
    }
  }

  async function loadGatewayAudioTracks(sourceUrl: string) {
    if (!import.meta.env.DEV) {
      return;
    }

    try {
      const response = await fetch(`/api/subtitles/probe?url=${encodeURIComponent(sourceUrl)}`);
      if (!response.ok) {
        return;
      }

      const probe = (await response.json()) as MediaTrackProbeResponse;
      const tracks = (probe.audioTracks ?? []).map((track, index) => {
        const audioTrackIndex = track.audioTrackIndex ?? index;
        return {
          value: makeTrackValue("gateway", audioTrackIndex),
          source: "gateway" as const,
          id: audioTrackIndex,
          label: track.label || track.language || `Audio ${index + 1}`,
          language: track.language,
        };
      });
      if (tracks.length <= 1) {
        return;
      }

      isUsingGatewayAudioTracksRef.current = true;
      setAudioTracks(tracks);
      const selected = tracks.find((track) => track.id === gatewayAudioTrackIndexRef.current) ?? tracks[0];
      gatewayAudioTrackIndexRef.current = selected.id;
      setSelectedAudioTrack(selected.value);
    } catch (error) {
      console.warn("[player] Gateway audio probe failed", error);
    }
  }

  function selectSubtitleTrack(value: string) {
    const previousGatewaySubtitleTrackIndex = gatewaySubtitleTrackIndexRef.current;
    selectedSubtitleTrackRef.current = value;
    setSelectedSubtitleTrack(value);
    const hls = hlsRef.current;
    const video = videoRef.current;
    if (!video) {
      return;
    }

    if (value === subtitlesOff) {
      cancelActiveSubtitleLoad();
      gatewaySubtitleTrackIndexRef.current = -1;
      if (hls) {
        hls.subtitleDisplay = false;
        hls.subtitleTrack = -1;
      }
      syncNativeSubtitleModes(video, -1);
      activeOverlaySubtitleCuesRef.current = [];
      overlaySubtitleIndexRef.current = -1;
      setOverlaySubtitleTextIfChanged("");
      if (isGatewayPlayback && previousGatewaySubtitleTrackIndex >= 0) {
        const targetTime = gatewayOffsetRef.current + (video.currentTime || 0);
        setSubtitleProbeMessage("Stanger av undertext...");
        void restartGatewayAt(targetTime);
      }
      return;
    }

    const parsedTrack = parseTrackValue(value);
    if (!parsedTrack) {
      return;
    }

    if (parsedTrack.source === "gateway-subtitle") {
      cancelActiveSubtitleLoad();
      gatewaySubtitleTrackIndexRef.current = parsedTrack.id;
      activeOverlaySubtitleCuesRef.current = [];
      overlaySubtitleIndexRef.current = -1;
      setOverlaySubtitleTextIfChanged("");
      if (isGatewayPlayback) {
        const targetTime = gatewayOffsetRef.current + (video.currentTime || 0);
        setSubtitleProbeMessage("Byter undertext...");
        void restartGatewayAt(targetTime);
      }
      return;
    }

    if (parsedTrack.source === "hls" && hls) {
      activeOverlaySubtitleCuesRef.current = [];
      setOverlaySubtitleTextIfChanged("");
      hls.subtitleDisplay = true;
      hls.subtitleTrack = parsedTrack.id;
      syncNativeSubtitleModes(video, parsedTrack.id);
      applyCurrentSubtitleOffset();
      return;
    }

    if (parsedTrack.source === "external") {
      const externalTrack = externalSubtitleTracks.find((track) => track.value === value);
      if (!externalTrack?.url) {
        return;
      }

      if (hls) {
        hls.subtitleDisplay = false;
        hls.subtitleTrack = -1;
      }
      syncNativeSubtitleModes(video, -1);
      cancelActiveSubtitleLoad();
      setSubtitleProbeMessage(`Laddar ${externalTrack.label}...`);
      void loadAndShowExternalSubtitleTrack(externalTrack, value);
      return;
    }

    if (parsedTrack.source === "native") {
      activeOverlaySubtitleCuesRef.current = [];
      setOverlaySubtitleTextIfChanged("");
      if (hls) {
        hls.subtitleDisplay = true;
      }
      syncNativeSubtitleModes(video, parsedTrack.id);
      applyCurrentSubtitleOffset();
    }
  }

  async function loadAndShowExternalSubtitleTrack(track: TrackOption, expectedSelection: string) {
    const video = videoRef.current;
    if (!video || !track.url) {
      return;
    }

    const loadedCues = loadedExternalSubtitlesRef.current.get(track.value);
    if (loadedCues) {
      showOverlaySubtitleTrack(loadedCues);
      setSubtitleProbeMessage("");
      return;
    }

    cancelActiveSubtitleLoad();
    const abortController = new AbortController();
    activeSubtitleLoadAbortRef.current = abortController;
    const expectedChannelUrl = activeChannelUrlRef.current;
    try {
      const content = await fetchText(track.url, abortController.signal);
      const cues = parseSubtitleCues(content);

      if (
        activeChannelUrlRef.current !== expectedChannelUrl
        || selectedSubtitleTrackRef.current !== expectedSelection
        || !videoRef.current
      ) {
        return;
      }

      loadedExternalSubtitlesRef.current.set(track.value, cues);
      showOverlaySubtitleTrack(cues);
      setSubtitleProbeMessage(cues.length > 0 ? "" : "Inga rader hittades i undertexten.");
    } catch (error) {
      if (isAbortError(error)) {
        return;
      }

      console.warn("[subtitles] Failed to load subtitle track", track.label, error);
      if (selectedSubtitleTrackRef.current === expectedSelection) {
        setSubtitleProbeMessage("Undertexten kunde inte laddas.");
      }
    } finally {
      if (activeSubtitleLoadAbortRef.current === abortController) {
        activeSubtitleLoadAbortRef.current = null;
      }
    }
  }

  function cancelActiveSubtitleLoad(message?: string) {
    if (!activeSubtitleLoadAbortRef.current) {
      return;
    }

    activeSubtitleLoadAbortRef.current.abort();
    activeSubtitleLoadAbortRef.current = null;
    if (message) {
      setSubtitleProbeMessage(message);
    }
  }

  function clearLoadedExternalSubtitles() {
    loadedExternalSubtitlesRef.current.clear();
    activeOverlaySubtitleCuesRef.current = [];
    overlaySubtitleIndexRef.current = -1;
    setOverlaySubtitleTextIfChanged("");
  }

  function showOverlaySubtitleTrack(cues: SubtitleCue[]) {
    activeOverlaySubtitleCuesRef.current = cues;
    overlaySubtitleIndexRef.current = -1;
    updateOverlaySubtitle();
    startOverlaySubtitleLoop();
  }

  function startOverlaySubtitleLoop() {
    stopOverlaySubtitleLoop();
    function tick() {
      updateOverlaySubtitle();
      overlaySubtitleFrameRef.current = window.requestAnimationFrame(tick);
    }

    overlaySubtitleFrameRef.current = window.requestAnimationFrame(tick);
  }

  function stopOverlaySubtitleLoop() {
    if (overlaySubtitleFrameRef.current) {
      window.cancelAnimationFrame(overlaySubtitleFrameRef.current);
      overlaySubtitleFrameRef.current = undefined;
    }
  }

  function updateOverlaySubtitle() {
    const video = videoRef.current;
    const cues = activeOverlaySubtitleCuesRef.current;
    if (!video || cues.length === 0) {
      setOverlaySubtitleTextIfChanged("");
      return;
    }

    const subtitleTime = (isGatewayPlayback ? gatewayOffsetRef.current : 0) + (video.currentTime || 0) + subtitleOffsetRef.current;
    const cueIndex = findSubtitleCueIndex(cues, subtitleTime, overlaySubtitleIndexRef.current);
    overlaySubtitleIndexRef.current = cueIndex;
    setOverlaySubtitleTextIfChanged(cueIndex >= 0 ? cues[cueIndex].text : "");
  }

  function setOverlaySubtitleTextIfChanged(value: string) {
    if (overlaySubtitleTextRef.current === value) {
      return;
    }

    overlaySubtitleTextRef.current = value;
    setOverlaySubtitleText(value);
  }

  function toggleFullscreen() {
    const host = videoRef.current?.closest(".player-screen");
    if (!host) {
      return;
    }

    if (document.fullscreenElement) {
      void document.exitFullscreen();
    } else {
      void host.requestFullscreen();
    }
  }

  function adjustSubtitleOffset(deltaSeconds: number) {
    setSubtitleOffsetSeconds((value) => {
      const nextValue = clamp(value + deltaSeconds, -maxSubtitleOffsetSeconds, maxSubtitleOffsetSeconds);
      return Math.round(nextValue * 10) / 10;
    });
    revealPlayerUi();
  }

  function resetSubtitleOffset() {
    setSubtitleOffsetSeconds(0);
    revealPlayerUi();
  }

  function applyCurrentSubtitleOffset() {
    const video = videoRef.current;
    if (!video) {
      return;
    }

    applySubtitleOffset(video, subtitleOffsetRef.current, originalSubtitleCueTimesRef.current);
    applySubtitleControlLift();
  }

  function applySubtitleControlLift() {
    const video = videoRef.current;
    if (!video) {
      return;
    }

    applySubtitleLineLift(video, isUiVisible, originalSubtitleCueLinesRef.current);
  }

  function revealPlayerUi() {
    setIsUiVisible(true);
    if (uiHideTimeoutRef.current) {
      window.clearTimeout(uiHideTimeoutRef.current);
    }

    if (trackPicker || isPointerOverPlayerChromeRef.current) {
      return;
    }

    uiHideTimeoutRef.current = window.setTimeout(() => {
      setIsUiVisible(false);
    }, uiAutoHideMs);
  }

  function keepPlayerChromeVisible() {
    isPointerOverPlayerChromeRef.current = true;
    setIsUiVisible(true);
    if (uiHideTimeoutRef.current) {
      window.clearTimeout(uiHideTimeoutRef.current);
      uiHideTimeoutRef.current = undefined;
    }
  }

  function releasePlayerChromeHover() {
    isPointerOverPlayerChromeRef.current = false;
    revealPlayerUi();
  }

  function closeTrackPickerAndResumeAutoHide() {
    setTrackPicker(undefined);
    isPointerOverPlayerChromeRef.current = false;
    setIsUiVisible(true);
    if (uiHideTimeoutRef.current) {
      window.clearTimeout(uiHideTimeoutRef.current);
    }
    uiHideTimeoutRef.current = window.setTimeout(() => {
      setIsUiVisible(false);
    }, uiAutoHideMs);
  }

  const timelineDuration = isGatewayPlayback
    ? getTimelineDurationLimit(channel.durationSeconds, knownTimelineDuration, gatewayOffset + duration)
    : duration;
  const displayCurrentTime = isGatewayPlayback ? gatewayOffset + currentTime : currentTime;
  const timelineValue = scrubTime ?? displayCurrentTime;
  const canSeek = timelineDuration > 0;
  const hasAudioChoices = audioTracks.length > 1;
  const subtitleTracks = [...externalSubtitleTracks, ...mediaSubtitleTracks];
  const hasSubtitleChoices = subtitleTracks.length > 0;
  const subtitleTrackOptions = [
    { value: subtitlesOff, source: "native" as const, id: -1, label: "Undertexter av" },
    ...subtitleTracks,
  ];
  const playerStatusMessage = subtitleProbeMessage || (showTrackDiagnostics ? trackDiagnostics : "");
  const subtitleOffsetLabel = `${subtitleOffsetSeconds > 0 ? "+" : ""}${subtitleOffsetSeconds.toFixed(1)}s`;
  const showBufferingOverlay = isBuffering && shouldAutoPlayRef.current;
  const effectiveUiVisible = isUiVisible || Boolean(trackPicker);

  return (
    <div
      className={`player-screen ${effectiveUiVisible ? "ui-visible" : "ui-hidden"}`}
      onPointerMove={revealPlayerUi}
      onTouchStart={revealPlayerUi}
    >
      <div className="player-topbar">
        <button className="icon-text-button" onClick={closePlayer} autoFocus>
          <ArrowLeft size={20} />
          Tillbaka
        </button>
        <div>
          <h1>{channel.name}</h1>
          <p>{channel.categoryName}</p>
        </div>
      </div>

      <div className="video-stage">
        <video
          ref={videoRef}
          className="video-player"
          playsInline
          autoPlay
          preload="auto"
          onClick={togglePlayback}
        />
        {showBufferingOverlay ? (
          <div className="player-buffering" role="status" aria-live="polite">
            <LoadingSpinner />
            <span>Buffrar...</span>
          </div>
        ) : null}
        {overlaySubtitleText ? (
          <div className={`subtitle-overlay ${effectiveUiVisible ? "controls-visible" : ""}`}>
            {overlaySubtitleText}
          </div>
        ) : null}
        {trackPicker === "audio" ? (
          <TrackPickerPanel
            icon={<Volume2 size={24} />}
            title="Audio"
            options={audioTracks}
            selectedValue={selectedAudioTrack}
            onSelect={(value) => {
              selectAudioTrack(value);
              closeTrackPickerAndResumeAutoHide();
            }}
            onClose={closeTrackPickerAndResumeAutoHide}
            onPointerEnter={keepPlayerChromeVisible}
            onPointerLeave={releasePlayerChromeHover}
          />
        ) : null}
        {trackPicker === "subtitle" ? (
          <TrackPickerPanel
            icon={<Captions size={24} />}
            title="Subtitle"
            options={subtitleTrackOptions}
            selectedValue={selectedSubtitleTrack}
            columns={subtitleTrackOptions.length > 7 ? 2 : 1}
            onSelect={(value) => {
              selectSubtitleTrack(value);
              closeTrackPickerAndResumeAutoHide();
            }}
            onClose={closeTrackPickerAndResumeAutoHide}
            onPointerEnter={keepPlayerChromeVisible}
            onPointerLeave={releasePlayerChromeHover}
          />
        ) : null}
        <div
          className="player-controls"
          onPointerEnter={keepPlayerChromeVisible}
          onPointerLeave={releasePlayerChromeHover}
        >
          <div className="player-timeline-row">
            <button className="round-control-button" onClick={togglePlayback} aria-label={isPlaying ? "Pausa" : "Spela"}>
              {isPlaying ? <Pause size={22} fill="currentColor" /> : <Play size={22} fill="currentColor" />}
            </button>

            <div className="time-stack">
              <input
                type="range"
                min={0}
                max={Math.max(0, timelineDuration)}
                step={0.25}
                value={Math.min(timelineValue, timelineDuration || timelineValue)}
                disabled={!canSeek}
                onChange={(event) => updateScrub(Number(event.target.value))}
                onPointerUp={commitScrub}
                onTouchEnd={commitScrub}
                onBlur={commitScrub}
                onKeyUp={commitScrub}
              />
              <span>{formatTime(timelineValue)} / {canSeek ? formatTime(timelineDuration) : "--:--"}</span>
            </div>
          </div>

          <div className="player-tools-row">
            {hasAudioChoices ? (
              <button className="icon-text-button track-picker-button" onClick={() => setTrackPicker("audio")}>
                <Volume2 size={17} />
                Audio
              </button>
            ) : null}

            {hasSubtitleChoices ? (
              <button className="icon-text-button track-picker-button" onClick={() => setTrackPicker("subtitle")}>
                <Captions size={17} />
                Subtitle
              </button>
            ) : null}

          {hasSubtitleChoices ? (
            <div className="subtitle-offset-controls" aria-label="Förskjut undertexter">
              <button type="button" onClick={() => adjustSubtitleOffset(-1)} aria-label="Undertexter en sekund tidigare">
                <Minus size={15} />
                <span>1s</span>
              </button>
              <button type="button" className="subtitle-offset-value" onClick={resetSubtitleOffset} aria-label="Nollställ undertextförskjutning">
                {subtitleOffsetLabel}
              </button>
              <button type="button" onClick={() => adjustSubtitleOffset(1)} aria-label="Undertexter en sekund senare">
                <Plus size={15} />
                <span>1s</span>
              </button>
            </div>
          ) : null}

            {playerStatusMessage ? (
              <span className="track-status-label">{playerStatusMessage}</span>
            ) : null}

            <button className="round-control-button" onClick={toggleFullscreen} aria-label="Fullscreen">
              <Maximize size={20} />
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}

function TrackPickerPanel({
  icon,
  title,
  options,
  selectedValue,
  columns = 1,
  onSelect,
  onClose,
  onPointerEnter,
  onPointerLeave,
}: {
  icon: ReactNode;
  title: string;
  options: TrackOption[];
  selectedValue: string;
  columns?: 1 | 2;
  onSelect: (value: string) => void;
  onClose: () => void;
  onPointerEnter: () => void;
  onPointerLeave: () => void;
}) {
  const panelRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    const buttons = getTrackPickerButtons(panelRef.current);
    const selectedButton = buttons.find((button) => button.dataset.value === selectedValue);
    (selectedButton ?? buttons[0])?.focus();
  }, [selectedValue]);

  function onPanelKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    if (event.key === "Escape" || event.key === "Backspace") {
      event.preventDefault();
      onClose();
      return;
    }

    if (!["ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight"].includes(event.key)) {
      return;
    }

    const buttons = getTrackPickerButtons(panelRef.current);
    const currentIndex = buttons.findIndex((button) => button === document.activeElement);
    if (currentIndex < 0) {
      buttons[0]?.focus();
      event.preventDefault();
      return;
    }

    const columnCount = columns === 2 ? 2 : 1;
    const delta = event.key === "ArrowDown"
      ? columnCount
      : event.key === "ArrowUp"
        ? -columnCount
        : event.key === "ArrowRight"
          ? 1
          : -1;
    const nextIndex = clamp(currentIndex + delta, 0, buttons.length - 1);
    buttons[nextIndex]?.focus();
    event.preventDefault();
  }

  return (
    <div className="track-picker-backdrop" onClick={onClose}>
      <section
        className="track-picker-panel"
        ref={panelRef}
        aria-label={title}
        onClick={(event) => event.stopPropagation()}
        onKeyDown={onPanelKeyDown}
        onPointerEnter={onPointerEnter}
        onPointerLeave={onPointerLeave}
      >
        <div className="track-picker-title">
          {icon}
          <h2>{title}</h2>
        </div>
        <div className={`track-picker-list ${columns === 2 ? "two-columns" : ""}`}>
          {options.map((option) => {
            const isSelected = option.value === selectedValue;
            return (
              <button
                className={`track-picker-option ${isSelected ? "selected" : ""}`}
                data-value={option.value}
                key={option.value}
                onClick={() => onSelect(option.value)}
              >
                <span>{option.label}</span>
                {isSelected ? <small>Vald</small> : null}
              </button>
            );
          })}
        </div>
      </section>
    </div>
  );
}

function getTrackPickerButtons(panel: HTMLDivElement | null) {
  return Array.from(panel?.querySelectorAll<HTMLButtonElement>(".track-picker-option") ?? []);
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

function clamp(value: number, min: number, max: number) {
  return Math.min(Math.max(value, min), max);
}

function delay(ms: number) {
  return new Promise((resolve) => window.setTimeout(resolve, ms));
}

function getTimelineDurationLimit(...values: Array<number | undefined>) {
  return values
    .filter((value): value is number => typeof value === "number" && Number.isFinite(value) && value > 0)
    .reduce((max, value) => Math.max(max, value), 0);
}

function getStreamKind(sourceUrl: string) {
  try {
    const parsed = new URL(sourceUrl, window.location.href);
    const extension = parsed.pathname.split(".").pop()?.toLowerCase() ?? "";
    return extension || "unknown";
  } catch {
    const match = /\.([a-z0-9]+)(?:$|[?#])/i.exec(sourceUrl);
    return match?.[1].toLowerCase() ?? "unknown";
  }
}

function findSubtitleCueIndex(cues: SubtitleCue[], time: number, currentIndex: number) {
  const currentCue = cues[currentIndex];
  if (currentCue && time >= currentCue.startTime && time <= currentCue.endTime) {
    return currentIndex;
  }

  const nextCue = cues[currentIndex + 1];
  if (nextCue && time >= nextCue.startTime && time <= nextCue.endTime) {
    return currentIndex + 1;
  }

  let low = 0;
  let high = cues.length - 1;
  while (low <= high) {
    const middle = Math.floor((low + high) / 2);
    const cue = cues[middle];
    if (time < cue.startTime) {
      high = middle - 1;
    } else if (time > cue.endTime) {
      low = middle + 1;
    } else {
      return middle;
    }
  }

  return -1;
}

function parseSubtitleCues(content: string) {
  const normalized = content
    .replace(/^\uFEFF/, "")
    .replace(/\r\n/g, "\n")
    .replace(/\r/g, "\n")
    .trim();
  if (!normalized) {
    return [];
  }

  return normalized
    .split(/\n{2,}/)
    .map(parseSubtitleBlock)
    .filter((cue): cue is SubtitleCue => Boolean(cue))
    .sort((left, right) => left.startTime - right.startTime);
}

function parseSubtitleBlock(block: string): SubtitleCue | undefined {
  const lines = block
    .split("\n")
    .map((line) => line.trimEnd())
    .filter((line) => line.trim() && !/^WEBVTT\b/i.test(line.trim()) && !/^NOTE\b/i.test(line.trim()));
  const timingIndex = lines.findIndex((line) => line.includes("-->"));
  if (timingIndex < 0) {
    return undefined;
  }

  const [rawStart, rawEnd] = lines[timingIndex].split("-->");
  const startTime = parseSubtitleTimestamp(rawStart);
  const endTime = parseSubtitleTimestamp(rawEnd);
  const text = lines
    .slice(timingIndex + 1)
    .map((line) => line.replace(/<[^>]*>/g, "").trim())
    .filter(Boolean)
    .join("\n");
  if (!Number.isFinite(startTime) || !Number.isFinite(endTime) || endTime <= startTime || !text) {
    return undefined;
  }

  return { startTime, endTime, text };
}

function parseSubtitleTimestamp(value: string) {
  const match = value.trim().match(/(?:(\d+):)?(\d{2}):(\d{2})[,.](\d{1,3})/);
  if (!match) {
    return Number.NaN;
  }

  const hours = Number(match[1] ?? 0);
  const minutes = Number(match[2]);
  const seconds = Number(match[3]);
  const milliseconds = Number(match[4].padEnd(3, "0"));
  return (hours * 3600) + (minutes * 60) + seconds + (milliseconds / 1000);
}

function applySubtitleOffset(
  video: HTMLVideoElement,
  offsetSeconds: number,
  originalCueTimes: WeakMap<TextTrackCue, OriginalTextTrackCueTime>,
) {
  Array.from(video.textTracks ?? []).forEach((track) => {
    const cues = track.cues;
    if (!cues) {
      return;
    }

    for (let index = 0; index < cues.length; index += 1) {
      const cue = cues[index] as MutableTextTrackCue;
      let originalTime = originalCueTimes.get(cue);
      if (!originalTime) {
        originalTime = {
          startTime: cue.startTime,
          endTime: cue.endTime,
        };
        originalCueTimes.set(cue, originalTime);
      }

      const nextStartTime = Math.max(0, originalTime.startTime + offsetSeconds);
      const nextEndTime = Math.max(nextStartTime + 0.05, originalTime.endTime + offsetSeconds);
      if (Math.abs(cue.startTime - nextStartTime) < 0.001 && Math.abs(cue.endTime - nextEndTime) < 0.001) {
        continue;
      }

      cue.startTime = nextStartTime;
      cue.endTime = nextEndTime;
    }
  });
}

function applySubtitleLineLift(
  video: HTMLVideoElement,
  shouldLift: boolean,
  originalLines: WeakMap<TextTrackCue, PositionedTextTrackCue["line"]>,
) {
  Array.from(video.textTracks ?? []).forEach((track) => {
    const cues = track.cues;
    if (!cues) {
      return;
    }

    for (let index = 0; index < cues.length; index += 1) {
      const cue = cues[index];
      if (!isPositionedTextTrackCue(cue)) {
        continue;
      }

      if (!originalLines.has(cue)) {
        originalLines.set(cue, cue.line);
      }

      const originalLine = originalLines.get(cue) ?? "auto";
      if (shouldLift) {
        cue.line = subtitleLineWithControlsVisible;
      } else if (originalLine === "auto") {
        cue.line = subtitleLineDefault;
      } else {
        cue.line = originalLine;
      }
    }
  });
}

function isPositionedTextTrackCue(cue: TextTrackCue): cue is PositionedTextTrackCue {
  return "line" in cue;
}

function makeTrackValue(source: TrackOption["source"], id: number) {
  return `${source}:${id}`;
}

function parseTrackValue(value: string): Pick<TrackOption, "source" | "id"> | undefined {
  const separatorIndex = value.lastIndexOf(":");
  const source = separatorIndex >= 0 ? value.slice(0, separatorIndex) : value;
  const rawId = separatorIndex >= 0 ? value.slice(separatorIndex + 1) : "";
  const id = Number(rawId);
  if (
    (
      source !== "hls"
      && source !== "native"
      && source !== "external"
      && source !== "gateway"
      && source !== "gateway-subtitle"
    )
    || !Number.isInteger(id)
  ) {
    return undefined;
  }

  return { source, id };
}

function getGatewaySubtitleTrackIndex(track: { id?: string; url?: string }) {
  const idMatch = track.id?.match(/^embedded:(\d+)$/);
  if (idMatch) {
    return Number(idMatch[1]);
  }

  if (!track.url?.startsWith("/api/subtitles/extract")) {
    return undefined;
  }

  try {
    const url = new URL(track.url, window.location.origin);
    const stream = Number(url.searchParams.get("stream"));
    return Number.isInteger(stream) && stream >= 0 ? stream : undefined;
  } catch {
    return undefined;
  }
}

function getSelectableTextTracks(video: HTMLVideoElement) {
  return Array.from(video.textTracks ?? [])
    .filter((track) => track.kind === "subtitles" || track.kind === "captions");
}

function syncNativeSubtitleModes(video: HTMLVideoElement, selectedIndex: number) {
  getSelectableTextTracks(video).forEach((track, index) => {
    track.mode = index === selectedIndex ? "showing" : "disabled";
  });
}

function dedupeSubtitleTracks<T extends { url: string }>(tracks: T[]) {
  const seenUrls = new Set<string>();
  return tracks.filter((track) => {
    if (seenUrls.has(track.url)) {
      return false;
    }

    seenUrls.add(track.url);
    return true;
  });
}

function isAbortError(error: unknown) {
  return error instanceof DOMException && error.name === "AbortError";
}

function logHlsEvent(message: string, data: unknown) {
  logPlayerEvent(message, summarizeHlsData(data));
}

function logPlayerEvent(message: string, data?: Record<string, unknown>) {
  if (!import.meta.env.DEV) {
    return;
  }

  void fetch("/api/client-log", {
    method: "POST",
    headers: {
      "content-type": "application/json",
    },
    body: JSON.stringify({
      scope: "player",
      message,
      data,
    }),
  }).catch(() => undefined);
}

function summarizeHlsData(data: unknown) {
  if (!data || typeof data !== "object") {
    return {};
  }

  const source = data as Record<string, unknown>;
  const summary: Record<string, unknown> = {};
  for (const key of ["type", "details", "fatal", "reason", "level", "id", "stats"]) {
    const value = source[key];
    if (value === undefined) {
      continue;
    }

    summary[key] = key === "stats" ? summarizeStats(value) : summarizeLogValue(value);
  }

  if (isRecord(source.frag)) {
    summary.frag = {
      sn: source.frag.sn,
      type: source.frag.type,
      level: source.frag.level,
      url: describeMediaUrl(String(source.frag.url ?? "")),
      duration: source.frag.duration,
      start: source.frag.start,
    };
  }

  if (isRecord(source.response)) {
    summary.response = {
      code: source.response.code,
      text: source.response.text,
      url: describeMediaUrl(String(source.response.url ?? "")),
    };
  }

  if (source.error instanceof Error) {
    summary.error = {
      name: source.error.name,
      message: source.error.message,
    };
  }

  return summary;
}

function summarizeStats(value: unknown) {
  if (!isRecord(value)) {
    return undefined;
  }

  return {
    loaded: value.loaded,
    total: value.total,
    loading: value.loading,
    parsing: value.parsing,
    buffering: value.buffering,
  };
}

function summarizeLogValue(value: unknown): unknown {
  if (
    value === null
    || typeof value === "string"
    || typeof value === "number"
    || typeof value === "boolean"
  ) {
    return value;
  }

  if (Array.isArray(value)) {
    return `array(${value.length})`;
  }

  if (isRecord(value)) {
    return value.constructor?.name ?? "object";
  }

  return typeof value;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === "object";
}

function describeMediaUrl(value: string) {
  if (!value) {
    return "";
  }

  try {
    const parsed = new URL(value, window.location.href);
    if (parsed.pathname.includes("/api/gateway/media/")) {
      return parsed.pathname;
    }

    return `${parsed.protocol}//${parsed.host}${parsed.pathname.split("/").slice(0, 3).join("/")}`;
  } catch {
    return value.startsWith("/api/") ? value : "(media-url)";
  }
}
