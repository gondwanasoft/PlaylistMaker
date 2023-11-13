# Playlist Player

A simplistic, crude and badly-written player for local video [.m3u playlists](https://wiki.videolan.org/M3U/) using [LibVLCSharp.WPF](https://code.videolan.org/videolan/LibVLCSharp) on Windows.

## Features

* No user interface and very few controls.
* Doesn't steal focus when changing clips (unlike VLC Media Player).
* Implements some VLC options that VLC Media Player ignores in playlists.
* Can be used in conjunction with [Playlist Maker](https://github.com/gondwanasoft/PlaylistMaker).

## Build

Use Microsoft Visual Studio 2022. You may need to install [LibVLCSharp.WPF](https://code.videolan.org/videolan/LibVLCSharp) packages.

## Usage

Playlist Player will play the .m3u file specified as its command line argument.

The easiest way to use Playlist Player is to establish an association between the .m3u filename extension and Playlist Player.

Playlist Player has very few controls. When it is running, press `h` or `?` to see the available options.

## File Format

You can create or edit .m3u files manually, or use [Playlist Maker](https://github.com/gondwanasoft/PlaylistMaker) to create playlists that are compatible with Playlist Player.

Playlist Player implements some of VLC's [undocumented](https://forum.videolan.org/viewtopic.php?p=530537) `#EXTVLCOPT` options (use `vlc --help` for clues). 
In addition, it implements its own non-standard `#EXPLPOPT` options for some whole-of-playlist options.

Here is an [Example.m3u](https://github.com/gondwanasoft/PlaylistPlayer/blob/master/Example.m3u).

Playlists that are modified for use in Playlist Player should still open in VLC Player (and presumably other .m3u-compatible programs), but unsupported options will be ignored.

### Whole-of-playlist Options

The following `#EXTVLCOPT:` lines can be used to specify options for all clips in the playlist (unless over-ridden):

* FULLSCREEN
* LOOP
* NO-LOOP
* VIDEO-ON-TOP
* NO-VIDEO-ON-TOP
* PLAY-AND-EXIT
* NO-PLAY-AND-EXIT

The following `#EXTPLPOPT:` lines can be used to specify options for all clips in the playlist (unless over-ridden):

* LOOP
* RATE=0.5
* INPUT-FAST-SEEK
* NO-AUDIO

### Per-clip Options

The following `#EXTVLCOPT:` lines can be used to specify options for a single clip (and will over-ride whole-of-playlist options):

* START-TIME=[seconds]
* STOP-TIME=[seconds]
* INPUT-REPEAT=[count]
* RATE=1
* RATE=0.5
* INPUT-FAST-SEEK
* AUDIO
* NO-AUDIO

### Recursion

PlaylistPlayer can recurse through playlists; _eg_, `my-master-playlist.m3u` could contain:
> my-playlist-1.m3u  
> my-playlist-2.m3u  
> my-playlist-3.m3u

## Default Options

You can specify whole-of-playlist options to be used by default (_ie_, if not specified in the .m3u being played).
To do so, create `PlaylistPlayer.m3u` in the same folder as the executable (`PlaylistPlayer.exe`), and populate it with your preferred whole-of-playlist options.
Playlist Player will read and process that file (if present) prior to loading the .m3u specified on the command line.