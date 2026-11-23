# Phone mic and camera page (Custom HTML widget)

A one-widget control for the phone mic and Plugcam: a big mic button (green live, red muted, grey while it connects),
a level meter, the apps using the mic or camera, and Start / Restart / Stop for Plugcam (Restart and Stop ask for a second tap).

## Use it
1. Settings › General › Phone mic › **Set up** (and **Set up** under Plugcam if you use it).
2. Settings › Shortcuts: add four shortcuts with these ids and commands:

   | id | command |
   |---|---|
   | `phone-mic` | `builtin:phone-mic` |
   | `plugcam-start` | `builtin:plugcam-start` |
   | `plugcam-restart` | `builtin:plugcam-restart` |
   | `plugcam-stop` | `builtin:plugcam-stop` |

3. Add a **Custom HTML** widget to a page and paste `mic-camera-widget.html` into it.

It reads `phoneMic`, `plugcam` and `privacy` from the agent's state and acts only through those shortcut ids, so it
stays inside the Custom HTML sandbox. Built-in widgets that fit next to it: Microphone selection, Output selection,
Microphone (on the cable's other end) and Push to talk.
