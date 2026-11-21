CRYSTAR  Crystar.QoL User Guide
────────────────────────────────────────────

Adds files only; no game files are changed.
Multipliers start at x1 and almost every switch is off,
so the game plays like vanilla until you change something.


1  Install
────────────────────────────────────────────
  1  Extract the Crystar.QoL folder
  2  Put the whole folder in the game folder (where CRYSTAR.exe is)
  3  Run install.cmd inside it
  4  Start the game from your Steam Library

  Wrong location? The installer searches upward and stops with a message
  if it cannot find the game folder.
  First launch is slower: the framework downloads IL2CPP proxy assemblies
  once, which requires an internet connection. If a "component download
  failed" window appears, enable an accelerator / switch network node and
  start the game again - downloads resume, and only need to succeed once.
  Players with working international access can ignore this note.

  Can't find the game folder? In Steam: right-click CRYSTAR
  -> Manage -> Browse local files.


2  Updating to a new version
────────────────────────────────────────────
  Already have an older version? Just install over it -
  no need to uninstall first:

  1  Close the game
  2  Extract the new package and put the whole Crystar.QoL
     folder in the game folder, replacing the old one
  3  Run install.cmd inside it
  4  Start the game from your Steam Library

  Your settings and save snapshots are kept, never overwritten.
  The installer prints "files will be updated" when it finds an
  existing install.
  Do not delete UserData\Crystar.QoL\backup\ after uninstalling,
  or your settings cannot be restored on the next install.


3  Menu and keys
────────────────────────────────────────────
  Press F2 to open / close the mod menu.
  With a controller, use the in-game menu instead:
  Config -> the bottom row "MOD Settings". Both menus are identical.

     F2          Open / close menu
     F8          Kill switch (turns every feature off)
     Q  E        Page
     Up/Dn W S   Select
     L/R  A D    Change value
     Enter       Apply (irreversible items ask twice)
     R           Reset this item
     Esc         Close

  Changes apply immediately and are saved when the menu closes.


4  What's in the menu
────────────────────────────────────────────
  Options are grouped; use Left/Right to change, Enter to apply:

     Core QoL       EXP / Money / Drop multipliers, drop rates,
                    kill count, auto pickup and range, move speed
     Combat assist  One-hit kill, damage multiplier, invincible,
                    always critical, Cry gauge, combo, Cry Mode, attack speed
     Economy        Unlock all shop items, Max out money, Spawn item
     Growth         Free purification / blending / transmute,
                    Unlock all skills, Torment pool options
     Exploration    Reveal whole map, Enemy icons on world map
     Flow           Auto-skip opening movie, Auto-skip cry scene
     Collection     Unlock all memoirs
     System         Language, Backup save, Reset all to defaults

  Game terms: money is Essence, the book is Memoirs of the Dead,
  and Growth options work on Torments and Sentiments.
  Items marked irreversible ask for confirmation first.
  Max out money, Spawn item and Unlock all memoirs change your save data;
  Unlock all memoirs has not been tested. Use "Backup save" first.


5  Uninstall
────────────────────────────────────────────
  Close the game, then run CrystarQoL_uninstall.cmd
  in the game folder and type YES.
  No game files or save data are touched.

  Your settings and save snapshots stay in the game folder's
  UserData\Crystar.QoL\backup\ ; the installer offers to restore them.
  For a clean removal, delete that backup folder and the extracted
  Crystar.QoL folder by hand.
