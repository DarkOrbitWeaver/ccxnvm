# 🔒 CCXNVM Secure Chat 🔒

```
  _____  _____  __  __  _   _  __      __  __  __
 / ____||  __ \|  \/  || \ | | \ \    / / |  \/  |
| |     | |__) | \  / ||  \| |  \ \  / /  | \  / |
| |     |  _  /| |\/| || . ` |   \ \/ /   | |\/| |
| |____ | | \ \| |  | || |\  |    \  /    | |  | |
 \_____||_|  \_\_|  |_||_| \_|     \/     |_|  |_|
```

## (⌐■_■) What Is This?

A zero-trust, end-to-end encrypted chat application that keeps your messages private and secure. No one can read your messages except you and your intended recipient - not even the server!

## (ﾉ◕ヮ◕)ﾉ*:･ﾟ✧ Features

* 🔐 End-to-end encryption using ChaCha20-Poly1305
* 🔏 Ed25519 signatures for message authentication
* 🔄 X25519 key exchange for perfect forward secrecy
* 👥 User registration with cryptographic verification
* 💾 Encrypted local vault for message storage
* 🕒 TOTP (Time-based One-Time Password) for two-factor authentication
* 📡 Real-time messaging via WebSockets

## (づ￣ ³￣)づ Quick Start Guide

### Server Setup

1. Navigate to the server directory:
```bash
cd ccxnvm/server
```

2. Install required packages:
```bash
pip install -r requirements.txt
```

3. Start the server:
```bash
python server.py
```

The server will be available at `http://localhost:8000`

### Client Setup

1. Navigate to the client directory:
```bash
cd ccxnvm/client
```

2. Install required packages:
```bash
pip install -r requirements.txt
```

3. Start the client:
```bash
python chat.py
```

4. Register a new account when prompted:
```
Username: [your username]
Password: [your password]
```

5. Scan the TOTP QR code with an authenticator app (Google Authenticator, Authy, etc.)

6. Start chatting securely! (◠‿◠)

## (❁´◡`❁) Usage Guide

### Command Reference

Once in the chat interface, you can use these commands:

* `/connect <username>` - Start a chat with another user
* `/msg <username> <message>` - Send a direct message
* `/help` - Show all available commands
* `/logout` - Safely log out and encrypt your message vault
* `/keygen` - Rotate your encryption keys (recommended every few months)

### Tips for Secure Usage

* (•̀ᴗ•́)و Always verify the recipient's public key before sending sensitive information
* (¬_¬") Don't share your TOTP secret with anyone
* (⊙_⊙) Be cautious of phishing attempts - the server will never ask for your password
* (˘▽˘)っ♨ Set a strong, unique password for your message vault

## (；￣Д￣) Troubleshooting

### Can't Connect to Server?

```
(╯°□°）╯︵ ┻━┻  "Connection Error!"
```

1. Make sure the server is running
2. Check that your internet connection is working
3. Verify server address and port are correct

### Authentication Issues?

```
(҂⌣̀_⌣́) "Auth Failed!"
```

1. Ensure your TOTP code is correct and in sync
2. Verify your username and password
3. Your session may have expired - try logging in again

## (⌐■_■) Security Features Explained

* Messages are encrypted using ChaCha20-Poly1305, a state-of-the-art encryption algorithm
* Every message is signed with your unique Ed25519 key to prevent tampering
* All data stored locally is encrypted with a key derived from your password
* The server never sees your actual message contents - only encrypted data

## (✿◠‿◠) Need Help?

* Check out our comprehensive tests to understand how the system works
* Run `python quick_test.py` to verify everything is working correctly
* The client has built-in help - just type `/help` when connected

---

```
   _____ _____            _____  _____ ______ 
  / ____|  __ \     /\   |  __ \|_   _|  ____|
 | (___ | |__) |   /  \  | |__) | | | | |__   
  \___ \|  _  /   / /\ \ |  _  /  | | |  __|  
  ____) | | \ \  / ____ \| | \ \ _| |_| |____ 
 |_____/|_|  \_\/_/    \_\_|  \_\_____|______|
                                               
```

Stay secure, chat privately! (◕‿◕)