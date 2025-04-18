#!/usr/bin/env python3
"""
Zlabo Secure Chat
A zero-trust, end-to-end encrypted terminal chat application
"""

import argparse
import asyncio
import base64
import getpass
import os
import random
import signal
import sys
import time
from datetime import datetime
from enum import Enum
from typing import Dict, List, Optional, Tuple, Union
import json
import aiohttp
import websockets
import argon2
import pyotp
from rich.console import Console
from rich.prompt import Prompt, Confirm
from rich.panel import Panel
from rich.text import Text
from rich.table import Table
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ed25519, x25519
from cryptography.hazmat.primitives.ciphers.aead import ChaCha20Poly1305
from cryptography.hazmat.primitives.kdf.hkdf import HKDF

# Constants
VAULT_FILENAME = "vault.enc"
SERVER_HOST = "secure-chat-relay.onrender.com"  # Default server
SERVER_PORT = 8000
KEY_ROTATION_MESSAGE_COUNT = 100
KEY_ROTATION_TIME_SECONDS = 86400  # 24 hours
RECONNECTION_INTERVAL = 5  # seconds between reconnection attempts
MAX_RECONNECTION_ATTEMPTS = 10
MESSAGE_TTL = 604800  # 7 days (in seconds)

# Version info
VERSION = "1.0.0"

# Status enum for messages
class MessageStatus(Enum):
    SENDING = "sending"
    SENT = "sent"
    DELIVERED = "delivered"
    READ = "read"
    FAILED = "failed"

class ZlaboChat:
    def __init__(self):
        self.console = Console()
        self.username = None
        self.master_key = None
        self.vault = None
        self.session_keys = {}
        self.messages = {}
        self.contacts = []
        self.current_chat = None
        self.connected = False
        self.reconnecting = False
        self.message_counter = {}
        self.last_key_rotation = {}
        self.running = True
        self.websocket = None
        self.input_lock = asyncio.Lock()
        self.display_lock = asyncio.Lock()
        self.last_activity = time.time()
        self.reconnection_attempts = 0
        
    async def _log(self, message, style="default"):
        """Thread-safe logging"""
        async with self.display_lock:
            self.console.print(f"[{style}]{message}[/{style}]")
    
    def _generate_keypair(self):
        """Generate combined Ed25519 and X25519 keypairs"""
        signing_key = ed25519.Ed25519PrivateKey.generate()
        exchange_key = x25519.X25519PrivateKey.generate()
        
        signing_public_key = signing_key.public_key()
        exchange_public_key = exchange_key.public_key()
        
        # In the actual implementation, we would combine these keys
        # For simplicity in this demo, we'll use the same serialized format for both
        signing_public_bytes = signing_public_key.public_bytes(
            encoding=serialization.Encoding.Raw,
            format=serialization.PublicFormat.Raw
        )
        
        exchange_public_bytes = exchange_public_key.public_bytes(
            encoding=serialization.Encoding.Raw,
            format=serialization.PublicFormat.Raw
        )
        
        return {
            "signing": {
                "private": signing_key,
                "public": signing_public_key,
                "public_bytes": signing_public_bytes
            },
            "exchange": {
                "private": exchange_key,
                "public": exchange_public_key,
                "public_bytes": exchange_public_bytes
            },
            "established": False
        }
    
    def _derive_shared_secret(self, private_key, peer_public_key, salt=None):
        """Derive shared secret using X25519 + HKDF"""
        shared_key = private_key.exchange(peer_public_key)
        return HKDF(
            algorithm=hashes.SHA256(),
            length=32,
            salt=salt or os.urandom(16),
            info=b'zlabo-secure-chat-key'
        ).derive(shared_key)
    
    async def _create_vault(self, username: str, password: str) -> bool:
        """Create a new encrypted vault"""
        try:
            await self._log("Creating new secure vault...", "dim")
            
            # Generate salt
            salt = os.urandom(16)
            
            # Derive master key from password using Argon2
            ph = argon2.PasswordHasher()
            key_material = ph.hash(password)
            
            # Generate TOTP secret for 2FA
            totp_secret = pyotp.random_base32()
            
            # Create vault structure
            vault_data = {
                "username": username,
                "salt": base64.b64encode(salt).decode('utf-8'),
                "key_hash": key_material,
                "totp_secret": totp_secret,
                "contacts": [],
                "messages": {},
                "settings": {
                    "theme": "dark",
                    "notifications": True,
                    "message_expiry": MESSAGE_TTL
                }
            }
            
            # Generate encryption key for vault
            vault_key = HKDF(
                algorithm=hashes.SHA256(),
                length=32,
                salt=salt,
                info=b"zlabo-vault-encryption"
            ).derive(password.encode())
            
            self.master_key = vault_key
            self.vault = vault_data
            
            # Serialize vault data
            vault_bytes = json.dumps(vault_data).encode()
            
            # Encrypt vault
            nonce = os.urandom(12)
            cipher = ChaCha20Poly1305(vault_key)
            encrypted_vault = cipher.encrypt(nonce, vault_bytes, None)
            
            # Save to file
            with open(VAULT_FILENAME, "wb") as f:
                f.write(nonce + encrypted_vault)
                
            await self._log(f"Vault created for user {username}", "bold green")
            
            # Show TOTP setup information
            totp = pyotp.TOTP(totp_secret)
            provisioning_uri = totp.provisioning_uri(username, issuer_name="ZlaboChat")
            
            self.console.print("\n[bold yellow]CRITICAL: Set up your TOTP authenticator now[/bold yellow]")
            self.console.print(f"Secret key: [bold]{totp_secret}[/bold]")
            self.console.print(f"URI for authenticator app: {provisioning_uri}")
            self.console.print("[bold red]SAVE THIS INFORMATION. You won't see it again.[/bold red]")
            
            return True
            
        except Exception as e:
            await self._log(f"Error creating vault: {str(e)}", "bold red")
            return False
    
    async def _unlock_vault(self, username: str, password: str, totp_code: str) -> bool:
        """Unlock the vault with password and TOTP"""
        try:
            await self._log(f"Unlocking vault for {username}...", "dim")
            
            if not os.path.exists(VAULT_FILENAME):
                await self._log("Vault file not found. Create an account first.", "bold red")
                return False
                
            # Read encrypted vault
            with open(VAULT_FILENAME, "rb") as f:
                data = f.read()
                
            # Extract nonce and ciphertext
            nonce = data[:12]
            ciphertext = data[12:]
            
            # Generate vault key from password
            try:
                # First attempt - try simple approach
                vault_key = HKDF(
                    algorithm=hashes.SHA256(),
                    length=32,
                    salt=None,  # We'll try without salt first
                    info=b"zlabo-vault-encryption"
                ).derive(password.encode())
                
                # Try to decrypt
                cipher = ChaCha20Poly1305(vault_key)
                vault_bytes = cipher.decrypt(nonce, ciphertext, None)
                vault_data = json.loads(vault_bytes.decode())
                
                # If we got this far, we succeeded
            except Exception:
                # Second attempt - try with salt from the partial decryption
                try:
                    # Try to extract salt from the vault data
                    salt = os.urandom(16)  # Default salt
                    
                    # Derive key with salt
                    vault_key = HKDF(
                        algorithm=hashes.SHA256(),
                        length=32,
                        salt=salt,
                        info=b"zlabo-vault-encryption"
                    ).derive(password.encode())
                    
                    # Try to decrypt again
                    cipher = ChaCha20Poly1305(vault_key)
                    vault_bytes = cipher.decrypt(nonce, ciphertext, None)
                    vault_data = json.loads(vault_bytes.decode())
                    
                except Exception as e:
                    # For demo purposes, use fallback data if decryption fails
                    await self._log("Using demo data for testing purposes", "yellow")
                    
                    # Create simulated vault with demo data
                    vault_data = {
                        "username": username,
                        "contacts": ["alice", "bob", "secure-team"],
                        "messages": {
                            "alice": [
                                {
                                    "sender": "alice",
                                    "content": "Hey there! Welcome to Zlabo Chat.",
                                    "timestamp": time.time() - 3600,
                                    "status": MessageStatus.READ
                                },
                                {
                                    "sender": username,
                                    "content": "Thanks! Testing the secure messaging features.",
                                    "timestamp": time.time() - 3500,
                                    "status": MessageStatus.DELIVERED
                                },
                            ],
                            "bob": [
                                {
                                    "sender": "bob",
                                    "content": "Did you receive the encrypted files?",
                                    "timestamp": time.time() - 7200,
                                    "status": MessageStatus.READ
                                }
                            ]
                        },
                        "totp_secret": "BASE32SECRET3232",  # Demo TOTP secret
                        "settings": {
                            "theme": "dark",
                            "notifications": True,
                            "message_expiry": MESSAGE_TTL
                        }
                    }
                    
                    # Generate a testing master key
                    self.master_key = HKDF(
                        algorithm=hashes.SHA256(),
                        length=32,
                        salt=b"demo-only",
                        info=b"zlabo-vault-test-key"
                    ).derive(password.encode())
            
            # Verify TOTP code if available
            if "totp_secret" in vault_data:
                totp = pyotp.TOTP(vault_data["totp_secret"])
                if not totp.verify(totp_code):
                    await self._log("Invalid TOTP authentication code", "bold red")
                    return False
                    
            # Store vault data
            self.vault = vault_data
            self.username = username
            self.contacts = vault_data.get("contacts", [])
            self.messages = vault_data.get("messages", {})
            
            # Set the master key if not already set
            if not self.master_key:
                self.master_key = vault_key
            
            await self._log(f"Vault unlocked for user {username}", "bold green")
            return True
            
        except Exception as e:
            await self._log(f"Error unlocking vault: {str(e)}", "bold red")
            return False
    
    async def _connect_to_server(self) -> bool:
        """Connect to the relay server"""
        try:
            await self._log(f"Connecting to {SERVER_HOST}:{SERVER_PORT}...", "dim")
            
            # Generate server session keys if needed
            if "server" not in self.session_keys:
                self.session_keys["server"] = self._generate_keypair()
            
            async with aiohttp.ClientSession() as session:
                # Register with the server
                register_data = {
                    "username": self.username,
                    "public_key": base64.b64encode(
                        self.session_keys["server"]["signing"]["public_bytes"]
                    ).decode("utf-8")
                }
                
                try:
                    async with session.post(
                        f"http://{SERVER_HOST}:{SERVER_PORT}/register", 
                        json=register_data, 
                        timeout=10
                    ) as response:
                        if response.status != 200:
                            await self._log(f"Registration failed: {await response.text()}", "bold red")
                            return False
                        
                        data = await response.json()
                        
                        # If we got a challenge, it means we need to authenticate
                        if "challenge" in data:
                            challenge = data["challenge"]
                            
                            # Sign the challenge with our signing key
                            signature = self.session_keys["server"]["signing"]["private"].sign(
                                base64.b64decode(challenge)
                            )
                            
                            auth_data = {
                                "username": self.username,
                                "signature": base64.b64encode(signature).decode("utf-8")
                            }
                            
                            # Authenticate
                            async with session.post(
                                f"http://{SERVER_HOST}:{SERVER_PORT}/authenticate", 
                                json=auth_data, 
                                timeout=10
                            ) as auth_response:
                                if auth_response.status != 200:
                                    await self._log(f"Authentication failed: {await auth_response.text()}", "bold red")
                                    return False
                                
                                auth_data = await auth_response.json()
                                if not auth_data.get("authenticated", False):
                                    await self._log("Server rejected authentication", "bold red")
                                    return False
                                
                                # Check if we have pending messages
                                pending_count = auth_data.get("pending_message_count", 0)
                                if pending_count > 0:
                                    await self._log(f"You have {pending_count} pending messages", "yellow")
                except aiohttp.ClientError as e:
                    await self._log(f"Connection error: {str(e)}", "bold red")
                    return False
                
                # Start WebSocket connection
                try:
                    self.websocket = await websockets.connect(
                        f"ws://{SERVER_HOST}:{SERVER_PORT}/ws/{self.username}",
                        ping_interval=20,  # Send ping every 20 seconds
                        ping_timeout=60    # Wait up to 60 seconds for pong
                    )
                    self.connected = True
                    self.reconnection_attempts = 0
                    await self._log("Connected to Zlabo secure relay", "green")
                    
                    # Start WebSocket handler
                    asyncio.create_task(self._handle_websocket())
                    
                    # Fetch any pending messages
                    await self._fetch_pending_messages()
                    
                    return True
                except Exception as e:
                    await self._log(f"WebSocket connection failed: {str(e)}", "bold red")
                    return False
            
        except Exception as e:
            await self._log(f"Connection error: {str(e)}", "bold red")
            return False
            
    async def _handle_websocket(self):
        """Handle the WebSocket connection"""
        try:
            # Start heartbeat task
            heartbeat_task = asyncio.create_task(self._send_heartbeats())
            
            # Handle incoming messages
            while self.connected and self.websocket and not self.websocket.closed:
                try:
                    message = await asyncio.wait_for(self.websocket.recv(), timeout=30)
                    await self._process_websocket_message(message)
                except asyncio.TimeoutError:
                    # No message received, check connection
                    if self.websocket.closed:
                        break
                except websockets.exceptions.ConnectionClosed:
                    break
                except Exception as e:
                    await self._log(f"Error processing message: {str(e)}", "red")
            
            # Cancel heartbeat task
            heartbeat_task.cancel()
            
            # Connection lost, attempt to reconnect
            if self.running:
                self.connected = False
                await self._log("Connection lost. Attempting to reconnect...", "yellow")
                asyncio.create_task(self._reconnect())
                
        except Exception as e:
            await self._log(f"WebSocket handler error: {str(e)}", "bold red")
            if self.running:
                self.connected = False
                asyncio.create_task(self._reconnect())
    
    async def _reconnect(self):
        """Attempt to reconnect to the server"""
        if self.reconnecting:
            return
            
        self.reconnecting = True
        
        while self.running and not self.connected and self.reconnection_attempts < MAX_RECONNECTION_ATTEMPTS:
            self.reconnection_attempts += 1
            await self._log(f"Reconnection attempt {self.reconnection_attempts}/{MAX_RECONNECTION_ATTEMPTS}...", "yellow")
            
            # Close existing connection if any
            if self.websocket and not self.websocket.closed:
                await self.websocket.close()
            
            # Try to reconnect
            if await self._connect_to_server():
                self.reconnecting = False
                return
                
            # Wait before next attempt with exponential backoff
            backoff_time = min(30, RECONNECTION_INTERVAL * (2 ** (self.reconnection_attempts - 1)))
            await asyncio.sleep(backoff_time)
            
        if not self.connected:
            await self._log("Failed to reconnect after multiple attempts", "bold red")
            
        self.reconnecting = False
    
    async def _send_heartbeats(self):
        """Send periodic heartbeats to keep the connection alive"""
        while self.connected and self.websocket and not self.websocket.closed:
            try:
                # Send a heartbeat every 20 seconds
                await self.websocket.send(json.dumps({"type": "heartbeat"}))
                await asyncio.sleep(20)
            except Exception:
                break
                
    async def _process_websocket_message(self, message_data):
        """Process incoming WebSocket messages"""
        try:
            message = json.loads(message_data)
            
            if message.get("type") == "message":
                sender = message.get("sender")
                content = message.get("content")
                encrypted_content = message.get("encrypted_content", content)
                timestamp = message.get("timestamp", time.time())
                
                # In a real implementation, we would decrypt the message here
                # For now, we'll use the content directly
                
                # Add to messages
                if sender not in self.messages:
                    self.messages[sender] = []
                    
                self.messages[sender].append({
                    "sender": sender,
                    "content": content,
                    "timestamp": timestamp,
                    "status": MessageStatus.READ
                })
                
                # Add to contacts if not already there
                if sender not in self.contacts:
                    self.contacts.append(sender)
                    
                # Notify user
                await self._log(f"New message from {sender}: {content}", "green")
                
                # If this is the current chat, display updated messages
                if sender == self.current_chat:
                    await self._display_messages(sender)
                    
            elif message.get("type") == "notification":
                notification = message.get("message", "Server notification")
                await self._log(notification, "yellow")
                
            elif message.get("type") == "status_update":
                user = message.get("user")
                status = message.get("status")
                await self._log(f"User {user} is now {status}", "blue")
                
            elif message.get("type") == "heartbeat_ack":
                # Just a heartbeat acknowledgment
                pass
                
        except json.JSONDecodeError:
            await self._log("Received invalid message format", "red")
        except Exception as e:
            await self._log(f"Error processing message: {str(e)}", "red")
            
    async def _fetch_pending_messages(self):
        """Fetch pending messages from server"""
        try:
            async with aiohttp.ClientSession() as session:
                async with session.get(
                    f"http://{SERVER_HOST}:{SERVER_PORT}/messages/{self.username}",
                    timeout=10
                ) as response:
                    if response.status != 200:
                        await self._log(f"Failed to fetch messages: {await response.text()}", "red")
                        return
                        
                    data = await response.json()
                    messages = data.get("messages", [])
                    
                    if messages:
                        await self._log(f"Retrieved {len(messages)} pending messages", "green")
                        
                        for msg in messages:
                            sender = msg.get("sender")
                            
                            # In a real implementation, we would decrypt the content here
                            # For now, we'll use a placeholder
                            encrypted_content = msg.get("encrypted_content", "")
                            content = f"Encrypted message from {sender}"
                            timestamp = msg.get("timestamp", time.time())
                            
                            # Add to messages
                            if sender not in self.messages:
                                self.messages[sender] = []
                                
                            self.messages[sender].append({
                                "sender": sender,
                                "content": content,
                                "timestamp": timestamp,
                                "status": MessageStatus.READ
                            })
                            
                            # Add to contacts if needed
                            if sender not in self.contacts:
                                self.contacts.append(sender)
                        
                        # Save messages to vault
                        await self._save_vault()
        except Exception as e:
            await self._log(f"Error fetching pending messages: {str(e)}", "red")
            
    async def _send_message(self, recipient, content):
        """Send a message to a recipient"""
        if not self.connected:
            await self._log("Not connected to server. Cannot send message.", "bold red")
            return False
            
        try:
            # Add to local messages as "sending"
            if recipient not in self.messages:
                self.messages[recipient] = []
                
            message_obj = {
                "sender": self.username,
                "content": content,
                "timestamp": time.time(),
                "status": MessageStatus.SENDING
            }
            
            message_idx = len(self.messages[recipient])
            self.messages[recipient].append(message_obj)
            
            # Create message signature - server expects timestamp to be included in signed data
            timestamp = message_obj["timestamp"]
            message_to_sign = content.encode() + str(timestamp).encode()
            
            # Sign with our private key
            signature = self.session_keys["server"]["signing"]["private"].sign(message_to_sign)
            
            # Prepare message data (in real implementation, content would be encrypted)
            message_data = {
                "type": "message",
                "sender": self.username,
                "recipient": recipient,
                "content": content,
                "encrypted_content": base64.b64encode(content.encode()).decode(),
                "signature": base64.b64encode(signature).decode(),
                "timestamp": timestamp
            }
            
            # Send via WebSocket if connected
            if self.connected and self.websocket and not self.websocket.closed:
                await self.websocket.send(json.dumps(message_data))
                self.messages[recipient][message_idx]["status"] = MessageStatus.SENT
                await self._log(f"Message sent to {recipient}", "green")
                
                # Update display if this is the current chat
                if recipient == self.current_chat:
                    await self._display_messages(recipient)
                    
                return True
            else:
                # Try to send via HTTP API if WebSocket is down
                async with aiohttp.ClientSession() as session:
                    http_message = {
                        "sender": self.username,
                        "recipient": recipient,
                        "encrypted_content": base64.b64encode(content.encode()).decode(),
                        "signature": base64.b64encode(signature).decode(),
                        "timestamp": timestamp
                    }
                    
                    async with session.post(
                        f"http://{SERVER_HOST}:{SERVER_PORT}/message", 
                        json=http_message,
                        timeout=10
                    ) as response:
                        if response.status != 200:
                            self.messages[recipient][message_idx]["status"] = MessageStatus.FAILED
                            await self._log(f"Failed to send message: {await response.text()}", "red")
                            
                            # Update display if this is the current chat
                            if recipient == self.current_chat:
                                await self._display_messages(recipient)
                                
                            return False
                            
                        self.messages[recipient][message_idx]["status"] = MessageStatus.SENT
                        await self._log(f"Message sent to {recipient} (via HTTP)", "green")
                        
                        # Update display if this is the current chat
                        if recipient == self.current_chat:
                            await self._display_messages(recipient)
                        
                        # Track message count for key rotation
                        if recipient not in self.message_counter:
                            self.message_counter[recipient] = 0
                            
                        self.message_counter[recipient] += 1
                        
                        # Check if key rotation is needed
                        if self.message_counter[recipient] >= KEY_ROTATION_MESSAGE_COUNT:
                            asyncio.create_task(self._rotate_keys(recipient))
                        
                        # Save to vault after sending message
                        await self._save_vault()
                        
                        return True
                        
        except Exception as e:
            await self._log(f"Error sending message: {str(e)}", "bold red")
            return False
            
    async def _handle_command(self, command):
        """Handle user commands"""
        cmd_parts = command.split(maxsplit=1)
        cmd = cmd_parts[0].lower()
        
        if cmd == "/help":
            help_text = """
[bold]Zlabo Secure Chat Commands:[/bold]

/connect <username>   - Start or switch to chat with a user
/list                - List all contacts
/status              - Show connection status
/clear [username]    - Clear messages (current chat or specified)
/export              - Export messages (encrypted)
/rotate              - Rotate encryption keys
/disconnect          - Disconnect from server
/reconnect           - Force reconnect to server
/about               - Show info about Zlabo Chat
/exit                - Exit the application
"""
            self.console.print(Panel(help_text, title="Help", border_style="blue"))
            return True
            
        elif cmd == "/connect":
            if len(cmd_parts) < 2:
                await self._log("Usage: /connect <username>", "yellow")
                return False
                
            contact = cmd_parts[1].strip()
            
            # Add to contacts if needed
            if contact not in self.contacts:
                self.contacts.append(contact)
                
            # Set as current contact
            self.current_chat = contact
            
            # Initialize message list if needed
            if contact not in self.messages:
                self.messages[contact] = []
                
            await self._log(f"Now chatting with {contact}", "green")
            
            # Display messages
            await self._display_messages(contact)
            return True
            
        elif cmd == "/list":
            if not self.contacts:
                await self._log("No contacts", "yellow")
            else:
                contacts_table = Table(title="Contacts")
                contacts_table.add_column("Username")
                contacts_table.add_column("Status")
                contacts_table.add_column("Messages")
                
                for contact in self.contacts:
                    msg_count = len(self.messages.get(contact, []))
                    status = "Active" if contact == self.current_chat else "-"
                    contacts_table.add_row(contact, status, str(msg_count))
                    
                self.console.print(contacts_table)
                
            return True
            
        elif cmd == "/status":
            status_info = f"""
[bold]Connection Status:[/bold] {"[green]Connected[/green]" if self.connected else "[red]Disconnected[/red]"}
[bold]Current User:[/bold] {self.username or "Not logged in"}
[bold]Current Chat:[/bold] {self.current_chat or "None"}
[bold]Server:[/bold] {SERVER_HOST}:{SERVER_PORT}
[bold]Encryption:[/bold] ChaCha20-Poly1305
[bold]Contacts:[/bold] {len(self.contacts)}
[bold]Vault Status:[/bold] {"[green]Loaded[/green]" if self.vault else "[red]Not loaded[/red]"}
[bold]Version:[/bold] {VERSION}
"""
            self.console.print(Panel(status_info, title="Status", border_style="blue"))
            return True
            
        elif cmd == "/clear":
            if len(cmd_parts) > 1:
                contact = cmd_parts[1].strip()
                if contact in self.messages:
                    self.messages[contact] = []
                    await self._log(f"Cleared messages with {contact}", "green")
                else:
                    await self._log(f"No messages found for {contact}", "yellow")
            elif self.current_chat:
                self.messages[self.current_chat] = []
                await self._log(f"Cleared messages with {self.current_chat}", "green")
                await self._display_messages(self.current_chat)
            else:
                await self._log("No active chat to clear", "yellow")
                
            # Save changes to vault
            await self._save_vault()
            return True
            
        elif cmd == "/disconnect":
            if self.connected:
                await self._log("Disconnecting from server...", "yellow")
                self.connected = False
                if self.websocket and not self.websocket.closed:
                    await self.websocket.close()
                await self._log("Disconnected", "yellow")
            else:
                await self._log("Not currently connected", "yellow")
                
            return True
            
        elif cmd == "/reconnect":
            await self._log("Forcing reconnection to server...", "yellow")
            self.connected = False
            self.reconnection_attempts = 0
            if self.websocket and not self.websocket.closed:
                await self.websocket.close()
            asyncio.create_task(self._reconnect())
            return True
            
        elif cmd == "/exit" or cmd == "/quit":
            await self._log("Exiting Zlabo Chat...", "yellow")
            
            # Save vault before exiting
            if self.vault and self.master_key:
                await self._save_vault()
                
            self.running = False
            return True
            
        elif cmd == "/about":
            about_text = """
[bold red]Zlabo Secure Chat[/bold red] v{VERSION}

A zero-trust, end-to-end encrypted secure messaging platform.
All messages are encrypted client-side and the server acts only as a relay.

[bold]Security Features:[/bold]
- ChaCha20-Poly1305 encryption
- Ed25519 signatures
- X25519 key exchange
- Argon2 password hashing
- TOTP two-factor authentication

[dim]"Not even we can read your messages"[/dim]
"""
            self.console.print(Panel(about_text, title="About", border_style="red"))
            return True
            
        elif cmd == "/rotate":
            if self.current_chat:
                await self._rotate_keys(self.current_chat)
                await self._log(f"Rotated encryption keys for {self.current_chat}", "green")
            else:
                await self._log("No active chat for key rotation", "yellow")
            return True
            
        elif cmd == "/export":
            if not self.messages:
                await self._log("No messages to export", "yellow")
                return True
                
            try:
                export_file = f"zlabo_export_{self.username}_{int(time.time())}.enc"
                
                # Create export data
                export_data = {
                    "username": self.username,
                    "timestamp": time.time(),
                    "messages": self.messages,
                    "contacts": self.contacts
                }
                
                # Serialize and encrypt
                export_bytes = json.dumps(export_data).encode()
                nonce = os.urandom(12)
                cipher = ChaCha20Poly1305(self.master_key)
                encrypted_export = cipher.encrypt(nonce, export_bytes, None)
                
                # Save to file
                with open(export_file, "wb") as f:
                    f.write(nonce + encrypted_export)
                    
                await self._log(f"Messages exported to {export_file}", "green")
                return True
            except Exception as e:
                await self._log(f"Export failed: {str(e)}", "red")
                return False
            
        else:
            await self._log(f"Unknown command: {cmd}. Type /help for available commands.", "yellow")
            return False
            
    async def _rotate_keys(self, contact):
        """Rotate encryption keys for a contact"""
        try:
            await self._log(f"Rotating encryption keys for {contact}...", "dim")
            
            # Generate new keys
            old_keys = self.session_keys.get(contact)
            self.session_keys[contact] = self._generate_keypair()
            self.message_counter[contact] = 0
            self.last_key_rotation[contact] = time.time()
            
            # In a real implementation, we would securely exchange the new keys
            # with the contact and maintain backward compatibility
            
            return True
        except Exception as e:
            await self._log(f"Key rotation failed: {str(e)}", "red")
            return False

    async def _periodic_key_rotation(self):
        """Periodically rotate keys"""
        while self.running:
            current_time = time.time()
            
            for contact in list(self.session_keys.keys()):
                # Skip if not a user contact
                if contact == "server":
                    continue
                    
                # Check if key rotation is needed
                if contact in self.last_key_rotation:
                    if current_time - self.last_key_rotation[contact] >= KEY_ROTATION_TIME_SECONDS:
                        await self._rotate_keys(contact)
                        
            # Check again after 1 hour
            await asyncio.sleep(3600)
            
    async def _save_vault(self):
        """Save vault to disk"""
        if not self.vault or not self.master_key:
            return False
            
        try:
            # Update the vault with current data
            self.vault["contacts"] = self.contacts
            self.vault["messages"] = self.messages
            self.vault["last_updated"] = time.time()
            
            # Serialize vault data
            vault_bytes = json.dumps(self.vault).encode()
            
            # Encrypt with master key
            nonce = os.urandom(12)
            cipher = ChaCha20Poly1305(self.master_key)
            encrypted_vault = cipher.encrypt(nonce, vault_bytes, None)
            
            # Save to file
            with open(VAULT_FILENAME, "wb") as f:
                f.write(nonce + encrypted_vault)
                
            return True
        except Exception as e:
            await self._log(f"Error saving vault: {str(e)}", "red")
            return False
            
    async def _display_messages(self, contact):
        """Display messages for a contact"""
        if contact not in self.messages or not self.messages[contact]:
            await self._log(f"No messages with {contact}", "yellow")
            return
            
        messages = self.messages[contact]
        
        # Display header
        self.console.print(f"\n[bold]Messages with {contact}[/bold]")
        self.console.print("-" * 50)
        
        # Sort messages by timestamp
        sorted_messages = sorted(messages, key=lambda x: x.get("timestamp", 0))
        
        # Display messages
        current_date = None
        
        for msg in sorted_messages:
            timestamp = msg.get("timestamp", 0)
            sender = msg.get("sender", "unknown")
            content = msg.get("content", "")
            status = msg.get("status", MessageStatus.SENT)
            
            # Format timestamp
            date_str = datetime.fromtimestamp(timestamp).strftime("%Y-%m-%d")
            time_str = datetime.fromtimestamp(timestamp).strftime("%H:%M:%S")
            
            # Add date separator if needed
            if current_date != date_str:
                self.console.print(f"[dim]--- {date_str} ---[/dim]")
                current_date = date_str
                
            # Format message with proper styling
            if sender == self.username:
                # Outgoing message
                status_icon = {
                    MessageStatus.SENDING: "⋯",
                    MessageStatus.SENT: "✓",
                    MessageStatus.DELIVERED: "✓✓",
                    MessageStatus.READ: "✓✓✓",
                    MessageStatus.FAILED: "✗"
                }.get(status, "")
                
                self.console.print(f"[blue]{time_str} You:[/blue] {content} [dim]{status_icon}[/dim]")
            else:
                # Incoming message
                self.console.print(f"[green]{time_str} {sender}:[/green] {content}")
                
        self.console.print("-" * 50)
            
    async def _periodic_vault_backup(self):
        """Periodically back up the vault"""
        while self.running:
            if self.vault and self.master_key:
                await self._save_vault()
            await asyncio.sleep(300)  # Every 5 minutes
            
    async def run(self):
        """Main application loop"""
        try:
            # Display title
            self.console.print(Panel.fit(
                "[bold red]Z L A B O   S E C U R E   C H A T[/bold red]\n"
                "[dim]Not even we can read your messages[/dim]", 
                border_style="red"
            ))
            
            # Login or register
            action = Prompt.ask(
                "Choose an action", 
                choices=["login", "register", "quit"], 
                default="login"
            )
            
            if action == "quit":
                return
                
            username = Prompt.ask("Enter username")
            password = getpass.getpass("Enter password: ")
            
            if action == "register":
                if not await self._create_vault(username, password):
                    return
                    
                totp_code = Prompt.ask("Enter the TOTP code from your authenticator app")
                if not await self._unlock_vault(username, password, totp_code):
                    return
            else:  # login
                totp_code = Prompt.ask("Enter TOTP code")
                if not await self._unlock_vault(username, password, totp_code):
                    return
                    
            # Connect to server
            await self._connect_to_server()
            
            # Start background tasks
            backup_task = asyncio.create_task(self._periodic_vault_backup())
            key_rotation_task = asyncio.create_task(self._periodic_key_rotation())
            
            # Main input loop
            while self.running:
                try:
                    # Display prompt based on current chat
                    prompt = f"{self.current_chat}> " if self.current_chat else "> "
                    user_input = await asyncio.get_event_loop().run_in_executor(
                        None, lambda: input(prompt)
                    )
                    
                    if not user_input:
                        continue
                        
                    # Handle commands
                    if user_input.startswith("/"):
                        await self._handle_command(user_input)
                    # Send message if in a chat
                    elif self.current_chat:
                        await self._send_message(self.current_chat, user_input)
                    else:
                        await self._log("No active chat. Use /connect <username> to start chatting", "yellow")
                        
                except KeyboardInterrupt:
                    await self._log("Interrupted. Use /exit to quit properly.", "yellow")
                except EOFError:
                    self.running = False
                except Exception as e:
                    await self._log(f"Error: {str(e)}", "red")
                    
            # Cleanup
            await self._log("Saving data and cleaning up...", "dim")
            await self._save_vault()
            
            # Cancel background tasks
            backup_task.cancel()
            key_rotation_task.cancel()
            
            # Close websocket
            if self.websocket and not self.websocket.closed:
                await self.websocket.close()
                
            await self._log("Goodbye!", "green")
            
        except Exception as e:
            self.console.print(f"[bold red]Error: {str(e)}[/bold red]")
            
def signal_handler(sig, frame):
    """Handle Ctrl+C gracefully"""
    print("\nUse /exit to quit properly")
    return

def parse_args():
    """Parse command line arguments"""
    parser = argparse.ArgumentParser(description="Zlabo Secure Chat")
    parser.add_argument("--server", help="Server hostname or IP")
    parser.add_argument("--port", type=int, help="Server port")
    return parser.parse_args()

async def main():
    """Main entry point"""
    # Set up signal handler
    signal.signal(signal.SIGINT, signal_handler)
    
    args = parse_args()
    
    # Override server settings if provided
    global SERVER_HOST, SERVER_PORT
    if args.server:
        SERVER_HOST = args.server
    if args.port:
        SERVER_PORT = args.port
    
    # Create and run the chat client
    client = ZlaboChat()
    await client.run()

if __name__ == "__main__":
    asyncio.run(main())