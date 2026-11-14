# 📱 Setup guide

No programming knowledge needed. This takes about 5 minutes.

## What you need

- **Telegram** on your phone ([iPhone](https://apps.apple.com/app/telegram-messenger/id686449807) / [Android](https://play.google.com/store/apps/details?id=org.telegram.messenger))
- A computer that stays on while you want notifications

---

## Step 1: Create your own Telegram bot

The notifications come from your own private bot. Telegram's official **BotFather** makes one for you.

1. Open Telegram and search for **@BotFather**. Pick the one with the **blue checkmark** ✔️. Others are fakes.

   ![Searching for BotFather](docs/setup/1-search-botfather.png)

2. Tap **Start**, then send:
   ```
   /newbot
   ```

3. BotFather asks for a **name**. This is what you'll see in your chat list, e.g. `My Marktplaats Alerts`.

4. Then it asks for a **username**. It has to end with `bot` and be unique, e.g. `jan_marktplaats_bot`.

   ![Choosing a name and username](docs/setup/2-newbot.png)

5. BotFather now sends you a **token**: a long code like `123456789:ABCdef...`. Copy it. You'll need it in step 3.

   ![The token message](docs/setup/3-token.png)

> ⚠️ **Keep your token secret.** Anyone who has it can control your bot. If you ever leak it, send `/revoke` to BotFather to get a new one.

---

## Step 2: Download the app

1. Go to the [**Releases**](../../releases) page of this project.
2. Download the file for your computer (e.g. `MarktplaatsNotifier-windows.zip`) and **unzip** it into a folder.

**Windows:** the first time you open it, you may see a blue **"Windows protected your PC"** screen. That's because the app isn't signed (signing costs money), not because it's dangerous. Click **More info → Run anyway**.

---

## Step 3: First launch

1. Double-click **MarktplaatsNotifier**. A black window opens.
2. It asks for your token. **Paste** it (right-click in the window) and press **Enter**.
3. It asks you to message your bot. Open your new bot in Telegram (BotFather sent you a link to it), tap **Start** or send `hi`.

   ![Sending the first message to your bot](docs/setup/4-message-bot.png)

4. The window says **"Setup done!"**. That's it! 🎉

The first round is quiet: the app remembers all current listings without sending them. From then on, you'll only get **new** listings.

![What the notifications look like](docs/screenshot.png)

---

## Step 4: Choose what to search for

Open **`searches.txt`** (in the same folder as the app) with Notepad. Every line is one search:

```
defect monitor;60
rtx 3060;110
```

That's the **search words**, a **`;`**, then the **max price in euros**. Add, change or remove lines, save, and restart the app.

**Tip:** after adding new searches, delete `seen.txt` before restarting. Otherwise you'll get all the old listings of the new search at once.

---

## Keep it running

The app only checks while the black window is **open** and your computer is **awake**. Closing the window stops the notifications.

---

## Something not working?

| Problem | Fix |
|---|---|
| Error with **401** or **404** right after pasting the token | The token is wrong. Delete `secrets.txt` and start the app again. |
| Nothing arrives | The first round is quiet. Wait a while, new listings don't appear every minute. |
| I want to start over completely | Delete `secrets.txt` and `seen.txt`, then start the app. |
| "Error with '...'" lines in the window | Usually a short internet hiccup. The app keeps going by itself. |
