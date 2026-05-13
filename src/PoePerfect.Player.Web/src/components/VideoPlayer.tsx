import Hls from "hls.js";
import { ArrowLeft, Captions, Maximize, Minus, Pause, Play, Plus, Volume2 } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import type { Channel } from "../domain";
import { startGatewayStream, stopGatewayStream } from "../services/gateway";
import { fetchText } from "../services/http";
import { isPreferredSubtitleTrack } from "../services/subtitles";

type VideoPlayerProps = {
  channel: Channel;
  onClose: () => void;
  onGatewaySessionChange?: (gateway: { sessionId: string; playlistUrl: string; startAtSeconds: number }) => void;
};

type TrackOption = {
  value: string;
  source: "hls" | "native" | "external";
  id: number;
  label: string;
  language?: string;
  url?: string;
};

type LoadedExternalSubtitleTrack = {
  element: HTMLTrackElement;
  objectUrl: string;
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

const noAudioTrack = "";
const subtitlesOff = "off";
const uiAutoHideMs = 2500;
const maxSubtitleOffsetSeconds = 30;

export function VideoPlayer({ channel, onClose, onGatewaySessionChange }: VideoPlayerProps) {
  const videoRef = useRef<HTMLVideoElement | null>(null);
  const hlsRef = useRef<Hls | null>(null);
  const activeGatewaySessionRef = useRef(channel.gatewaySessionId);
  const activeChannelUrlRef = useRef(channel.url);
  const gatewayOffsetRef = useRef(channel.gatewayStartOffsetSeconds ?? 0);
  const selectedSubtitleTrackRef = useRef(subtitlesOff);
  const shouldRestoreGatewaySubtitleRef = useRef(false);
  const activeSubtitleLoadAbortRef = useRef<AbortController | null>(null);
  const loadedExternalSubtitlesRef = useRef(new Map<string, LoadedExternalSubtitleTrack>());
  const subtitleCueOffsetsRef = useRef(new WeakMap<TextTrackCue, number>());
  const subtitleOffsetRef = useRef(0);
  const uiHideTimeoutRef = useRef<number | undefined>();
  const shouldAutoPlayRef = useRef(true);
  const [activeSourceUrl, setActiveSourceUrl] = useState(channel.url);
  const [gatewayOffset, setGatewayOffset] = useState(channel.gatewayStartOffsetSeconds ?? 0);
  const [isPlaying, setIsPlaying] = useState(false);
  const [currentTime, setCurrentTime] = useState(0);
  const [duration, setDuration] = useState(0);
  const [audioTracks, setAudioTracks] = useState<TrackOption[]>([]);
  const [mediaSubtitleTracks, setMediaSubtitleTracks] = useState<TrackOption[]>([]);
  const [externalSubtitleTracks, setExternalSubtitleTracks] = useState<TrackOption[]>([]);
  const [selectedAudioTrack, setSelectedAudioTrack] = useState(noAudioTrack);
  const [selectedSubtitleTrack, setSelectedSubtitleTrack] = useState(subtitlesOff);
  const [subtitleProbeMessage, setSubtitleProbeMessage] = useState("");
  const [scrubTime, setScrubTime] = useState<number | undefined>();
  const [subtitleOffsetSeconds, setSubtitleOffsetSeconds] = useState(0);
  const [isUiVisible, setIsUiVisible] = useState(true);
  const isGatewayPlayback = Boolean(channel.gatewaySessionId && channel.originalUrl);

  useEffect(() => {
    const nextOffset = channel.gatewayStartOffsetSeconds ?? 0;
    activeGatewaySessionRef.current = channel.gatewaySessionId;
    gatewayOffsetRef.current = nextOffset;
    setGatewayOffset(nextOffset);
    setActiveSourceUrl(channel.url);
    setScrubTime(undefined);
    shouldAutoPlayRef.current = true;
    shouldRestoreGatewaySubtitleRef.current = false;
  }, [channel.gatewaySessionId, channel.gatewayStartOffsetSeconds, channel.url]);

  useEffect(() => {
    selectedSubtitleTrackRef.current = selectedSubtitleTrack;
  }, [selectedSubtitleTrack]);

  useEffect(() => {
    subtitleOffsetRef.current = subtitleOffsetSeconds;
    applyCurrentSubtitleOffset();
  }, [activeSourceUrl, subtitleOffsetSeconds]);

  useEffect(() => {
    revealPlayerUi();
    return () => {
      if (uiHideTimeoutRef.current) {
        window.clearTimeout(uiHideTimeoutRef.current);
      }
    };
  }, [activeSourceUrl]);

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
    clearLoadedExternalSubtitles();
    subtitleCueOffsetsRef.current = new WeakMap<TextTrackCue, number>();
    setAudioTracks([]);
    setMediaSubtitleTracks([]);
    setExternalSubtitleTracks([]);
    setSelectedAudioTrack(noAudioTrack);
    setSelectedSubtitleTrack(subtitlesOff);
    setSubtitleProbeMessage("");

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

      const loadedExternalTextTracks = new Set(
        Array.from(loadedExternalSubtitlesRef.current.values()).map((track) => track.element.track),
      );
      const nativeSubtitleTracks = getSelectableTextTracks(player)
        .filter((track) => !loadedExternalTextTracks.has(track));
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

      const preferredExternalTracks = externalTracks.filter(isPreferredSubtitleTrack);
      if (preferredExternalTracks.length === 0) {
        setSubtitleProbeMessage(`Hittade ${externalTracks.length} undertextspar, men inga svenska eller engelska.`);
        return;
      }

      setExternalSubtitleTracks(
        preferredExternalTracks.map((track, index) => ({
          value: makeTrackValue("external", index),
          source: "external",
          id: index,
          label: track.label,
          language: track.language,
          url: track.url,
        })),
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
      if (error) {
        setSubtitleProbeMessage(`Video-fel: ${error.message || `kod ${error.code}`}`);
      }
    }

    function reportVideoWaiting() {
      logPlayerEvent("video.waiting", {
        currentTime: player.currentTime,
        networkState: player.networkState,
        readyState: player.readyState,
      });
    }

    function reportVideoPlaying() {
      logPlayerEvent("video.playing", {
        currentTime: player.currentTime,
        readyState: player.readyState,
      });
      if (isGatewayPlayback) {
        setSubtitleProbeMessage("");
      }
    }

    function reportVideoStalled() {
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
        });
      }
    }

    if (activeSourceUrl.includes(".m3u8") && Hls.isSupported()) {
      logPlayerEvent("hls.start", {
        url: describeMediaUrl(activeSourceUrl),
        nativeHls: canPlayNativeHls || "",
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
        const tracks = data.audioTracks.map((track, index) => ({
          value: makeTrackValue("hls", index),
          source: "hls" as const,
          id: index,
          label: track.name || track.lang || `Ljud ${index + 1}`,
        }));
        setAudioTracks(tracks);
        setSelectedAudioTrack(tracks[hls?.audioTrack ?? 0]?.value ?? tracks[0]?.value ?? noAudioTrack);
      });
      hls.on(Hls.Events.AUDIO_TRACK_SWITCHED, () => {
        setSelectedAudioTrack(makeTrackValue("hls", hls?.audioTrack ?? 0));
      });
      hls.on(Hls.Events.SUBTITLE_TRACKS_UPDATED, (_, data) => {
        const tracks = data.subtitleTracks.map((track, index) => ({
          value: makeTrackValue("hls", index),
          source: "hls" as const,
          id: index,
          label: track.name || track.lang || `Undertext ${index + 1}`,
        }));
        setMediaSubtitleTracks(tracks);
        const shouldRestoreGatewaySubtitle = isGatewayPlayback && shouldRestoreGatewaySubtitleRef.current && tracks.length > 0;
        hls!.subtitleDisplay = shouldRestoreGatewaySubtitle;
        hls!.subtitleTrack = shouldRestoreGatewaySubtitle ? 0 : -1;
        setSelectedSubtitleTrack(shouldRestoreGatewaySubtitle ? tracks[0].value : subtitlesOff);
      });
      hls.on(Hls.Events.SUBTITLE_TRACKS_CLEARED, () => {
        setMediaSubtitleTracks([]);
        setSelectedSubtitleTrack(subtitlesOff);
      });
      hls.on(Hls.Events.SUBTITLE_TRACK_SWITCH, (_, data) => {
        setSelectedSubtitleTrack(data.id >= 0 ? makeTrackValue("hls", data.id) : subtitlesOff);
        syncNativeSubtitleModes(player, data.id);
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
          setSubtitleProbeMessage(`Gateway/player-fel: ${data.details}`);
        }
      });
      hls.on(Hls.Events.MANIFEST_PARSED, () => {
        logPlayerEvent("hls.manifest_parsed", {
          audioTracks: hls?.audioTracks.length ?? 0,
          subtitleTracks: hls?.subtitleTracks.length ?? 0,
        });
        refreshNativeTracks();
        applyCurrentSubtitleOffset();
        ensurePlayback("manifest_parsed");
      });
    } else {
      hlsRef.current = null;
      video.src = activeSourceUrl;
      video.addEventListener("loadedmetadata", refreshNativeTracks);
      ensurePlayback("direct_source");
    }

    const nativeAudioTracks = (player as HTMLVideoElement & { audioTracks?: NativeAudioTrackList }).audioTracks;
    nativeAudioTracks?.addEventListener("addtrack", refreshNativeAudioTracks);
    nativeAudioTracks?.addEventListener("change", refreshNativeAudioTracks);
    player.textTracks?.addEventListener("addtrack", refreshNativeSubtitleTracks);
    player.textTracks?.addEventListener("change", refreshNativeSubtitleTracks);
    void loadExternalSubtitleTracks();

    const updatePlaying = () => setIsPlaying(!video.paused);
    const updateTime = () => {
      setCurrentTime(video.currentTime || 0);
      setDuration(Number.isFinite(video.duration) ? video.duration : 0);
    };
    const ensurePlaybackFromLoadedData = () => ensurePlayback("loadeddata");
    const ensurePlaybackFromCanPlay = () => ensurePlayback("canplay");

    video.addEventListener("play", updatePlaying);
    video.addEventListener("pause", updatePlaying);
    video.addEventListener("timeupdate", updateTime);
    video.addEventListener("durationchange", updateTime);
    video.addEventListener("loadeddata", ensurePlaybackFromLoadedData);
    video.addEventListener("canplay", ensurePlaybackFromCanPlay);
    video.addEventListener("seeking", cancelActiveSubtitleLoadForSeek);
    video.addEventListener("error", reportVideoError);
    video.addEventListener("waiting", reportVideoWaiting);
    video.addEventListener("playing", reportVideoPlaying);
    video.addEventListener("stalled", reportVideoStalled);

    return () => {
      isDisposed = true;
      hls?.destroy();
      hlsRef.current = null;
      cancelActiveSubtitleLoad();
      clearLoadedExternalSubtitles();
      video.removeEventListener("play", updatePlaying);
      video.removeEventListener("pause", updatePlaying);
      video.removeEventListener("timeupdate", updateTime);
      video.removeEventListener("durationchange", updateTime);
      video.removeEventListener("loadeddata", ensurePlaybackFromLoadedData);
      video.removeEventListener("canplay", ensurePlaybackFromCanPlay);
      video.removeEventListener("seeking", cancelActiveSubtitleLoadForSeek);
      video.removeEventListener("error", reportVideoError);
      video.removeEventListener("waiting", reportVideoWaiting);
      video.removeEventListener("playing", reportVideoPlaying);
      video.removeEventListener("stalled", reportVideoStalled);
      nativeAudioTracks?.removeEventListener("addtrack", refreshNativeAudioTracks);
      nativeAudioTracks?.removeEventListener("change", refreshNativeAudioTracks);
      video.textTracks?.removeEventListener("addtrack", refreshNativeSubtitleTracks);
      video.textTracks?.removeEventListener("change", refreshNativeSubtitleTracks);
      video.removeAttribute("src");
      video.load();
    };
  }, [activeSourceUrl, channel.contentType, channel.subtitleTracks, isGatewayPlayback]);

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

    const targetTime = clamp(value, 0, channel.durationSeconds ?? Math.max(value, 0));
    shouldRestoreGatewaySubtitleRef.current = selectedSubtitleTrackRef.current !== subtitlesOff;
    shouldAutoPlayRef.current = true;
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

  function selectSubtitleTrack(value: string) {
    selectedSubtitleTrackRef.current = value;
    setSelectedSubtitleTrack(value);
    const hls = hlsRef.current;
    const video = videoRef.current;
    if (!video) {
      return;
    }

    if (value === subtitlesOff) {
      cancelActiveSubtitleLoad();
      if (hls) {
        hls.subtitleDisplay = false;
        hls.subtitleTrack = -1;
      }
      syncNativeSubtitleModes(video, -1);
      return;
    }

    const parsedTrack = parseTrackValue(value);
    if (!parsedTrack) {
      return;
    }

    if (parsedTrack.source === "hls" && hls) {
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
      cancelActiveSubtitleLoad();
      setSubtitleProbeMessage(`Laddar ${externalTrack.label}...`);
      void loadAndShowExternalSubtitleTrack(externalTrack, value);
      return;
    }

    if (parsedTrack.source === "native") {
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

    const loadedTrack = loadedExternalSubtitlesRef.current.get(track.value);
    if (loadedTrack) {
      showExternalSubtitleTrack(video, loadedTrack.element);
      setSubtitleProbeMessage("");
      return;
    }

    cancelActiveSubtitleLoad();
    const abortController = new AbortController();
    activeSubtitleLoadAbortRef.current = abortController;
    const expectedChannelUrl = activeChannelUrlRef.current;
    try {
      const content = await fetchText(track.url, abortController.signal);
      const subtitleBlobUrl = URL.createObjectURL(
        new Blob([toWebVtt(content)], { type: "text/vtt" }),
      );

      if (
        activeChannelUrlRef.current !== expectedChannelUrl
        || selectedSubtitleTrackRef.current !== expectedSelection
        || !videoRef.current
      ) {
        URL.revokeObjectURL(subtitleBlobUrl);
        return;
      }

      const trackElement = document.createElement("track");
      trackElement.kind = "subtitles";
      trackElement.label = track.label;
      if (track.language) {
        trackElement.srclang = track.language;
      }
      trackElement.addEventListener("load", () => applyCurrentSubtitleOffset(), { once: true });
      trackElement.src = subtitleBlobUrl;
      videoRef.current.appendChild(trackElement);
      loadedExternalSubtitlesRef.current.set(track.value, {
        element: trackElement,
        objectUrl: subtitleBlobUrl,
      });
      showExternalSubtitleTrack(videoRef.current, trackElement);
      applyCurrentSubtitleOffset();
      setSubtitleProbeMessage("");
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

  function cancelActiveSubtitleLoadForSeek() {
    if (!activeSubtitleLoadAbortRef.current) {
      return;
    }

    selectedSubtitleTrackRef.current = subtitlesOff;
    setSelectedSubtitleTrack(subtitlesOff);
    if (videoRef.current) {
      syncNativeSubtitleModes(videoRef.current, -1);
    }
    cancelActiveSubtitleLoad("Undertextsladdning avbruten vid spolning. Valj text igen nar filmen spelar.");
  }

  function clearLoadedExternalSubtitles() {
    loadedExternalSubtitlesRef.current.forEach((track) => {
      track.element.remove();
      URL.revokeObjectURL(track.objectUrl);
    });
    loadedExternalSubtitlesRef.current.clear();
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

    applySubtitleOffset(video, subtitleOffsetRef.current, subtitleCueOffsetsRef.current);
  }

  function revealPlayerUi() {
    setIsUiVisible(true);
    if (uiHideTimeoutRef.current) {
      window.clearTimeout(uiHideTimeoutRef.current);
    }

    uiHideTimeoutRef.current = window.setTimeout(() => {
      setIsUiVisible(false);
    }, uiAutoHideMs);
  }

  const timelineDuration = isGatewayPlayback
    ? channel.durationSeconds ?? gatewayOffset + duration
    : duration;
  const displayCurrentTime = isGatewayPlayback ? gatewayOffset + currentTime : currentTime;
  const timelineValue = scrubTime ?? displayCurrentTime;
  const canSeek = timelineDuration > 0;
  const hasAudioChoices = audioTracks.length > 1;
  const subtitleTracks = [...mediaSubtitleTracks, ...externalSubtitleTracks];
  const hasSubtitleChoices = subtitleTracks.length > 0;
  const shouldShowSubtitleProbeMessage = import.meta.env.DEV && Boolean(subtitleProbeMessage);
  const subtitleOffsetLabel = `${subtitleOffsetSeconds > 0 ? "+" : ""}${subtitleOffsetSeconds.toFixed(1)}s`;

  return (
    <div
      className={`player-screen ${isUiVisible ? "ui-visible" : "ui-hidden"}`}
      onPointerMove={revealPlayerUi}
      onTouchStart={revealPlayerUi}
    >
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
        <video ref={videoRef} className="video-player" playsInline autoPlay preload="auto" />
        <div className="player-controls">
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

          {hasAudioChoices ? (
            <label className="track-select-label">
              <Volume2 size={17} />
              <select
                value={selectedAudioTrack}
                onChange={(event) => selectAudioTrack(event.target.value)}
              >
                {audioTracks.map((track) => (
                  <option value={track.value} key={track.value}>{track.label}</option>
                ))}
              </select>
            </label>
          ) : null}

          {hasSubtitleChoices ? (
            <label className="track-select-label">
              <Captions size={17} />
              <select
                value={selectedSubtitleTrack}
                onChange={(event) => selectSubtitleTrack(event.target.value)}
              >
                <option value={subtitlesOff}>Undertexter av</option>
                {subtitleTracks.map((track) => (
                  <option value={track.value} key={track.value}>{track.label}</option>
                ))}
              </select>
            </label>
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

          {shouldShowSubtitleProbeMessage ? (
            <span className="track-status-label">{subtitleProbeMessage}</span>
          ) : null}

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

function clamp(value: number, min: number, max: number) {
  return Math.min(Math.max(value, min), max);
}

function delay(ms: number) {
  return new Promise((resolve) => window.setTimeout(resolve, ms));
}

function applySubtitleOffset(
  video: HTMLVideoElement,
  offsetSeconds: number,
  appliedOffsets: WeakMap<TextTrackCue, number>,
) {
  Array.from(video.textTracks ?? []).forEach((track) => {
    const cues = track.cues;
    if (!cues) {
      return;
    }

    for (let index = 0; index < cues.length; index += 1) {
      const cue = cues[index] as MutableTextTrackCue;
      const appliedOffset = appliedOffsets.get(cue) ?? 0;
      const delta = offsetSeconds - appliedOffset;
      if (Math.abs(delta) < 0.001) {
        continue;
      }

      const nextStartTime = Math.max(0, cue.startTime + delta);
      const nextEndTime = Math.max(nextStartTime + 0.05, cue.endTime + delta);
      cue.startTime = nextStartTime;
      cue.endTime = nextEndTime;
      appliedOffsets.set(cue, offsetSeconds);
    }
  });
}

function makeTrackValue(source: TrackOption["source"], id: number) {
  return `${source}:${id}`;
}

function parseTrackValue(value: string): Pick<TrackOption, "source" | "id"> | undefined {
  const [source, rawId] = value.split(":");
  const id = Number(rawId);
  if ((source !== "hls" && source !== "native" && source !== "external") || !Number.isInteger(id)) {
    return undefined;
  }

  return { source, id };
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

function showExternalSubtitleTrack(video: HTMLVideoElement, selectedTrackElement: HTMLTrackElement) {
  getSelectableTextTracks(video).forEach((track) => {
    track.mode = track === selectedTrackElement.track ? "showing" : "disabled";
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

function toWebVtt(content: string) {
  const normalized = content
    .replace(/^\uFEFF/, "")
    .replace(/\r\n/g, "\n")
    .replace(/\r/g, "\n")
    .trimStart();

  if (/^WEBVTT/i.test(normalized)) {
    return normalized;
  }

  const convertedTimings = normalized.replace(
    /(\d{2}:\d{2}:\d{2}),(\d{3})/g,
    "$1.$2",
  );
  return `WEBVTT\n\n${convertedTimings}`;
}
