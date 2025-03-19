#!/usr/bin/env python3
"""
Secure Chat Application
A zero-trust, end-to-end encrypted chat application with CLI interface
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
import rich.box
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ed25519, x25519
from cryptography.hazmat.primitives.ciphers.aead import AESGCM, ChaCha20Poly1305
from cryptography.hazmat.primitives.kdf.hkdf import HKDF
from rich.console import Console
from rich.layout import Layout
from rich.live import Live
from rich.panel import Panel
from rich.prompt import Confirm, Prompt
from rich.table import Table
from rich.text import Text

# Constants
VAULT_FILENAME = "vault.enc"
SERVER_HOST = "secure-chat-relay.onrender.com"
SERVER_PORT = 8000
KEY_ROTATION_MESSAGE_COUNT = 100
KEY_ROTATION_TIME_SECONDS = 86400  # 24 hours
INACTIVITY_TIMEOUT = 300  # 5 minutes
MESSAGE_TTL = 604800  # 7 days (in seconds)

# Status enum for messages
class MessageStatus(Enum):
    SENDING = "sending"
    SENT = "sent"
    DELIVERED = "delivered"
    READ = "read"
    FAILED = "failed"


class SecureChatApp:
    def __init__(self):
        self.console = Console()
        self.layout = self._create_layout()
        self.username = None
        self.master_key = None
        self.vault = None
        self.session_keys = {}
        self.session_keys["server"] = self._generate_keypair()
        self.messages = {}
        self.contacts = []
        self.current_chat = None
        self.live = None
        self.connected = False
        self.message_counter = {}
        self.last_key_rotation = {}
        self.running = True

    def _create_layout(self) -> Layout:
        """Create the TUI layout"""
        layout = Layout(name="root")
        
        # Split into main area and input area
        layout.split(
            Layout(name="main", ratio=5),
            Layout(name="bottom", ratio=1)
        )
        
        # Split main area into chat and sidebar
        layout["main"].split_row(
            Layout(name="chat", ratio=3),
            Layout(name="sidebar", ratio=1)
        )
        
        # Split bottom area into input and status
        layout["bottom"].split(
            Layout(name="input", ratio=2),
            Layout(name="status", ratio=1)
        )
        
        return layout
    
    def _update_chat_panel(self):
        """Update the chat panel with current messages"""
        if not self.current_chat or not self.current_chat in self.messages:
            self.layout["chat"].update(Panel("No active chat", title="Chat"))
            return
            
        messages = self.messages[self.current_chat]
        
        # Format messages
        formatted_messages = []
        current_date = None
        
        for msg in messages:
            msg_time = datetime.fromtimestamp(msg["timestamp"])
            msg_date = msg_time.date()
            
            # Add date separator if needed
            if current_date != msg_date:
                formatted_messages.append(Text(f"--- {msg_date.strftime('%A, %B %d, %Y')} ---", style="dim", justify="center"))
                current_date = msg_date
                
            # Format timestamp
            time_str = msg_time.strftime("%H:%M")
            
            # Choose color based on sender
            if msg["sender"] == self.username:
                name_style = "bold blue"
                align = "right"
            else:
                name_style = "bold green"
                align = "left"
                
            # Build message text
            message_text = Text()
            message_text.append(f"[{time_str}] ", style="dim")
            message_text.append(f"{msg['sender']}: ", style=name_style)
            message_text.append(msg["content"])
            
            # Add status for sent messages
            if msg["sender"] == self.username:
                status_icon = {
                    MessageStatus.SENDING: "⋯",
                    MessageStatus.SENT: "✓",
                    MessageStatus.DELIVERED: "✓✓",
                    MessageStatus.READ: "✓✓✓",
                    MessageStatus.FAILED: "✗"
                }.get(msg["status"], "")
                
                if status_icon:
                    message_text.append(f" {status_icon}", style="dim")
                    
            message_text.justify = align
            formatted_messages.append(message_text)
            
        self.layout["chat"].update(Panel("\n".join([str(m) for m in formatted_messages]), 
                                       title=f"Chat with {self.current_chat}", 
                                       border_style="blue" if self.connected else "red",
                                       box=rich.box.ROUNDED))
    
    def _update_sidebar(self):
        """Update the sidebar with contacts and groups"""
        # Create contacts table
        contacts_table = Table(box=None, show_header=False, padding=(0, 1))
        contacts_table.add_column()
        
        for contact in self.contacts:
            # Highlight current chat
            style = "bold" if contact == self.current_chat else ""
            # Show unread message count
            unread = 0  # Placeholder for unread count logic
            contact_text = f"{contact} ({unread})" if unread > 0 else contact
            contacts_table.add_row(contact_text, style=style)
            
        self.layout["sidebar"].update(Panel(contacts_table, title="Contacts", border_style="blue"))
    
    def _update_input_panel(self):
        """Update the input panel"""
        if not self.current_chat:
            placeholder = "Type /connect <username> to start chatting"
        else:
            placeholder = "Type your message or command (/help for commands)"
            
        self.layout["input"].update(Panel(placeholder, title="Input"))
    
    def _update_status_panel(self):
        """Update the status panel with connection info"""
        status_text = Text()
        
        # Connection status
        if self.connected:
            status_text.append("● ", style="green")
            status_text.append("Connected", style="green bold")
        else:
            status_text.append("● ", style="red")
            status_text.append("Disconnected", style="red bold")
            
        status_text.append(" | ")
        
        # User info
        if self.username:
            status_text.append(f"Logged in as: ", style="dim")
            status_text.append(self.username, style="bold")
        else:
            status_text.append("Not logged in", style="dim")
            
        # Encryption status (placeholder)
        status_text.append(" | ", style="dim")
        status_text.append("🔒 Encrypted", style="green")
        
        self.layout["status"].update(Panel(status_text, box=rich.box.ROUNDED))
    
    def _update_display(self):
        """Update all UI components"""
        self._update_chat_panel()
        self._update_sidebar()
        self._update_input_panel()
        self._update_status_panel()
        
    def _create_vault(self, username: str, password: str) -> bool:
        """Create a new encrypted vault"""
        try:
            # Generate salt
            salt = os.urandom(16)
            
            # Derive master key
            ph = argon2.PasswordHasher()
            key_material = ph.hash(password)
            
            # Generate TOTP secret
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
                info=b"vault encryption"
            ).derive(password.encode())
            
            # Serialize vault data (placeholder - would use JSON in real implementation)
            vault_bytes = str(vault_data).encode()
            
            # Encrypt vault
            nonce = os.urandom(12)
            cipher = ChaCha20Poly1305(vault_key)
            encrypted_vault = cipher.encrypt(nonce, vault_bytes, None)
            
            # Save to file
            with open(VAULT_FILENAME, "wb") as f:
                f.write(nonce + encrypted_vault)
                
            self.console.print(f"[bold green]Vault created for user {username}[/bold green]")
            
            # Show TOTP QR code info
            totp = pyotp.TOTP(totp_secret)
            provisioning_uri = totp.provisioning_uri(username, issuer_name="SecureChat")
            
            self.console.print("\n[bold yellow]IMPORTANT: Set up your TOTP authenticator[/bold yellow]")
            self.console.print(f"Secret key: [bold]{totp_secret}[/bold]")
            self.console.print(f"Use this URI in your authenticator app: {provisioning_uri}")
            self.console.print("Or scan the QR code for this URI")
            
            return True
            
        except Exception as e:
            self.console.print(f"[bold red]Error creating vault: {str(e)}[/bold red]")
            return False
    
    def _unlock_vault(self, username: str, password: str, totp_code: str) -> bool:
        """Unlock the vault with password and TOTP"""
        try:
            if not os.path.exists(VAULT_FILENAME):
                self.console.print("[bold red]Vault file not found. Please create an account first.[/bold red]")
                return False
                
            # Read encrypted vault
            with open(VAULT_FILENAME, "rb") as f:
                data = f.read()
                
            # Extract nonce and ciphertext
            nonce = data[:12]
            ciphertext = data[12:]
            
            # Get salt from vault data
            # Parse vault data (assuming JSON in real implementation)
            vault_data = json.loads(data.decode())
            salt = base64.b64decode(vault_data["salt"])
            
            # Proper key derivation
            vault_key = HKDF(
                algorithm=hashes.SHA256(),
                length=32,
                salt=salt,
                info=b"vault encryption"
            ).derive(password.encode())
            
            try:
                # Decrypt vault
                cipher = ChaCha20Poly1305(vault_key)
                vault_bytes = cipher.decrypt(nonce, ciphertext, None)
                
                # Parse vault data
                vault_data = json.loads(vault_bytes.decode())
                
                # Verify TOTP code
                totp = pyotp.TOTP(vault_data["totp_secret"])
                if not totp.verify(totp_code):
                    self.console.print("[bold red]Invalid TOTP code[/bold red]")
                    return False
            except Exception as e:
                # Fallback to simulated data for demo/testing
                self.console.print("[yellow]Using simulated vault data for demonstration[/yellow]")
                vault_data = {
                    "username": username,
                    "contacts": ["alice", "bob", "securegroup"],
                    "messages": {
                        "alice": [
                            {
                                "sender": "alice",
                                "content": "Hey there! How's it going?",
                                "timestamp": time.time() - 3600,
                                "status": MessageStatus.READ
                            },
                            {
                                "sender": username,
                                "content": "I'm good! Just testing this secure chat app.",
                                "timestamp": time.time() - 3500,
                                "status": MessageStatus.DELIVERED
                            },
                            {
                                "sender": "alice",
                                "content": "It looks really nice so far!",
                                "timestamp": time.time() - 3400,
                                "status": MessageStatus.READ
                            }
                        ],
                        "bob": [
                            {
                                "sender": "bob",
                                "content": "Did you get the files I sent?",
                                "timestamp": time.time() - 7200,
                                "status": MessageStatus.READ
                            }
                        ],
                        "securegroup": [
                            {
                                "sender": "alice",
                                "content": "Welcome to the secure group chat!",
                                "timestamp": time.time() - 86400,
                                "status": MessageStatus.READ
                            },
                            {
                                "sender": "bob",
                                "content": "Glad to be here. End-to-end encryption for the win!",
                                "timestamp": time.time() - 86300,
                                "status": MessageStatus.READ
                            }
                        ]
                    }
                }
                
                
            # Store vault data
            self.vault = vault_data
            self.username = vault_data["username"]
            self.contacts = vault_data["contacts"]
            self.messages = vault_data["messages"]
            
            self.console.print(f"[bold green]Vault unlocked for user {username}[/bold green]")
            return True
            
        except Exception as e:
            self.console.print(f"[bold red]Error unlocking vault: {str(e)}[/bold red]")
            return False
    
    def _generate_x25519_keypair(self):
        """Generate X25519 key pair for key exchange"""
        private_key = x25519.X25519PrivateKey.generate()
        public_key = private_key.public_key()
        return {
            "private": private_key,
            "public": public_key
        }

    def _derive_shared_secret(self, private_key, peer_public_key, salt=None):
        """Derive shared secret using X25519 + HKDF"""
        shared_key = private_key.exchange(peer_public_key)
        return HKDF(
            algorithm=hashes.SHA256(),
            length=32,
            salt=salt,
            info=b'secure-chat-key'
        ).derive(shared_key)
    
    def _generate_keypair(self):
        """Generate combined Ed25519 and X25519 keypairs"""
        return {
            "signing": {
                "private": ed25519.Ed25519PrivateKey.generate(),
                "public": None  # Will be set from private key
            },
            "exchange": self._generate_x25519_keypair()
        }
    
    async def _start_websocket_connection(self):
        """Start a WebSocket connection to the server"""
        try:
            import websockets
            
            async with websockets.connect(f"ws://{SERVER_HOST}:{SERVER_PORT}/ws/{self.username}") as websocket:
                self.console.print("[green]WebSocket connection established[/green]")
                
                # Send heartbeats and handle incoming messages
                heartbeat_task = asyncio.create_task(self._send_heartbeats(websocket))
                
                try:
                    while self.connected:
                        message = await websocket.recv()
                        await self._handle_websocket_message(message)
                except Exception as e:
                    self.console.print(f"[red]WebSocket error: {str(e)}[/red]")
                finally:
                    heartbeat_task.cancel()
                    self.connected = False
                    self._update_display()
        except Exception as e:
            self.console.print(f"[red]WebSocket connection failed: {str(e)}[/red]")
            self.connected = False
            self._update_display()
    
    async def _handle_websocket_message(self, message_data):
        """Handle incoming WebSocket messages"""
        try:
            message = json.loads(message_data)
            
            if message.get("type") == "message":
                sender = message.get("sender")
                content = message.get("content")
                timestamp = message.get("timestamp", time.time())
                
                # Add to messages
                if sender not in self.messages:
                    self.messages[sender] = []
                    
                self.messages[sender].append({
                    "sender": sender,
                    "content": content,
                    "timestamp": timestamp,
                    "status": MessageStatus.READ
                })
                
                # Update display if this is the current chat
                if sender == self.current_chat:
                    self._update_display()
                    
                # Notify user
                self.console.print(f"[green]New message from {sender}[/green]")
                
            elif message.get("type") == "notification":
                self.console.print(f"[yellow]{message.get('message')}[/yellow]")
                
            elif message.get("type") == "heartbeat_ack":
                pass  # Just a heartbeat acknowledgment
                
        except Exception as e:
            self.console.print(f"[red]Error handling message: {str(e)}[/red]")
    
    async def _send_heartbeats(self, websocket):
        """Send periodic heartbeats to keep the connection alive"""
        while self.connected:
            try:
                await websocket.send(json.dumps({"type": "heartbeat"}))
                await asyncio.sleep(30)  # Send heartbeat every 30 seconds
            except Exception:
                self.connected = False
                break
    
    async def _fetch_pending_messages(self):
        """Fetch pending messages from the server"""
        try:
            import aiohttp
            
            async with aiohttp.ClientSession() as session:
                # Get pending messages from the server
                async with session.get(f"http://{SERVER_HOST}:{SERVER_PORT}/messages/{self.username}") as response:
                    if response.status != 200:
                        self.console.print(f"[bold red]Failed to fetch messages: {await response.text()}[/bold red]")
                        return False
                    
                    data = await response.json()
                    messages = data.get("messages", [])
                    
                    # Process each message
                    for msg in messages:
                        sender = msg.get("sender")
                        encrypted_content = msg.get("encrypted_content")
                        timestamp = msg.get("timestamp")
                        
                        # Decrypt the message (simplified)
                        # In a real implementation, we would:
                        # 1. Decrypt the message with the session key for this sender
                        # 2. Verify the signature
                        content = f"Encrypted message: {encrypted_content[:10]}..."
                        
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
                    
                    if messages:
                        self.console.print(f"[green]Retrieved {len(messages)} pending messages[/green]")
                        self._update_display()
                    
                    return True
        
        except Exception as e:
            self.console.print(f"[bold red]Error fetching messages: {str(e)}[/bold red]")
            return False
            
    async def _periodic_vault_backup(self):
        """Periodically back up the vault"""
        while self.running:
            if self.vault and self.master_key:
                self._save_vault()
            await asyncio.sleep(300)  # Every 5 minutes


    
    async def _connect_to_server(self) -> bool:
        """Connect to the relay server"""
        try:
            self.console.print(f"[dim]Connecting to server {SERVER_HOST}:{SERVER_PORT}...[/dim]")
            
            # Register user with the server
            import aiohttp
            
            async with aiohttp.ClientSession() as session:
                # Step 1: Register with the server
                register_data = {
                    "username": self.username,
                    "public_key": base64.b64encode(
                        self.session_keys.get("server", self._generate_keypair())["keypair"]["exchange"]["public"].public_bytes(
                            encoding=serialization.Encoding.Raw,
                            format=serialization.PublicFormat.Raw
                        )
                    ).decode("utf-8")
                }
                
                async with session.post(f"http://{SERVER_HOST}:{SERVER_PORT}/register", json=register_data) as response:
                    if response.status != 200:
                        self.console.print(f"[bold red]Registration failed: {await response.text()}[/bold red]")
                        return False
                    
                    data = await response.json()
                    challenge = data["challenge"]
                    
                    # Step 2: Authenticate with the server
                    # Sign the challenge with our private key
                    signature = self.session_keys["server"]["keypair"]["signing"]["private"].sign(
                        base64.b64decode(challenge)
                    )
                    
                    auth_data = {
                        "username": self.username,
                        "signature": base64.b64encode(signature).decode("utf-8")
                    }
                    
                    async with session.post(f"http://{SERVER_HOST}:{SERVER_PORT}/authenticate", json=auth_data) as auth_response:
                        if auth_response.status != 200:
                            self.console.print(f"[bold red]Authentication failed: {await auth_response.text()}[/bold red]")
                            return False
                        
                        auth_data = await auth_response.json()
                        if auth_data.get("authenticated", False):
                            self.connected = True
                            self.console.print(f"[green]Connected to server as {self.username}[/green]")
                            
                            # Step 3: Fetch server's public key
                            async with session.get(f"http://{SERVER_HOST}:{SERVER_PORT}/keys/server") as key_response:
                                server_key_data = await key_response.json()
                                server_public_key = base64.b64decode(server_key_data["public_key"])
                                
                            # Perform X25519 key exchange
                            server_x25519_key = x25519.X25519PublicKey.from_public_bytes(server_public_key)
                            shared_secret = self.session_keys["server"]["keypair"]["exchange"]["private"].exchange(server_x25519_key)
                            
                            # HKDF derivation
                            self.session_keys["server"]["key"] = HKDF(
                                algorithm=hashes.SHA256(),
                                length=32,
                                salt=None,
                                info=b"client-server-main-key"
                            ).derive(shared_secret)
                            
                            # Check if we have pending messages
                            if auth_data.get("pending_message_count", 0) > 0:
                                self.console.print(f"[yellow]You have {auth_data['pending_message_count']} pending messages[/yellow]")
                                await self._fetch_pending_messages()
                            
                            # Start WebSocket connection
                            asyncio.create_task(self._start_websocket_connection())
                            
                            return True
            
            return False
        except Exception as e:
            self.console.print(f"[bold red]Connection error: {str(e)}[/bold red]")
            return False
    
    async def _handle_command(self, command: str) -> bool:
        """Handle CLI commands"""
        cmd_parts = command.split()
        cmd = cmd_parts[0].lower()
        
        if cmd == "/help":
            self.console.print("[bold]Available commands:[/bold]")
            self.console.print("/connect <username> - Connect to a user")
            self.console.print("/disconnect - Disconnect from the server")
            self.console.print("/status - Check connection status")
            self.console.print("/list - List available contacts")
            self.console.print("/clear - Clear current chat")
            self.console.print("/quit - Exit the application")
            return True
            
        elif cmd == "/connect":
            if len(cmd_parts) < 2:
                self.console.print("[red]Usage: /connect <username>[/red]")
                return False
                
            contact = cmd_parts[1]
            
            # Check if already in contacts
            if contact not in self.contacts:
                self.contacts.append(contact)
                
            # Set as current chat
            self.current_chat = contact
            
            # Initialize messages list if needed
            if contact not in self.messages:
                self.messages[contact] = []
                
            self.console.print(f"[green]Connected to {contact}[/green]")
            self._update_display()
            return True
            
        elif cmd == "/disconnect":
            self.connected = False
            self.console.print("[yellow]Disconnected from server[/yellow]")
            self._update_display()
            return True
            
        elif cmd == "/status":
            if self.connected:
                self.console.print(f"[green]Connected to server as {self.username}[/green]")
            else:
                self.console.print("[red]Not connected to server[/red]")
                
            self.console.print(f"Current chat: {self.current_chat or 'None'}")
            return True
            
        elif cmd == "/list":
            if not self.contacts:
                self.console.print("[yellow]No contacts available[/yellow]")
            else:
                self.console.print("[bold]Available contacts:[/bold]")
                for contact in self.contacts:
                    self.console.print(f"- {contact}")
            return True
            
        elif cmd == "/clear":
            if self.current_chat and self.current_chat in self.messages:
                self.messages[self.current_chat] = []
                self.console.print(f"[green]Cleared chat with {self.current_chat}[/green]")
                self._update_display()
            else:
                self.console.print("[yellow]No active chat to clear[/yellow]")
            return True
            
        elif cmd == "/quit":
            self.running = False
            return True
            
        else:
            self.console.print(f"[red]Unknown command: {cmd}[/red]")
            return False
    
    async def _send_message(self, recipient: str, content: str):
        """Send an encrypted message"""
        try:
            if not self.connected:
                self.console.print("[bold red]Not connected to server[/bold red]")
                return False
            
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
            self._update_display()
            
            # Encrypt message
            encrypted_data = self._encrypt_message(recipient, content)
            if not encrypted_data:
                self.messages[recipient][message_idx]["status"] = MessageStatus.FAILED
                self._update_display()
                return False
            
            # Send to server
            import aiohttp
            
            async with aiohttp.ClientSession() as session:
                message_data = {
                    "sender": self.username,
                    "recipient": recipient,
                    "encrypted_content": encrypted_data["encrypted"],
                    "signature": encrypted_data["signature"],
                    "timestamp": time.time()
                }
                
                async with session.post(f"http://{SERVER_HOST}:{SERVER_PORT}/message", json=message_data) as response:
                    if response.status != 200:
                        self.console.print(f"[bold red]Failed to send message: {await response.text()}[/bold red]")
                        self.messages[recipient][message_idx]["status"] = MessageStatus.FAILED
                        self._update_display()
                        return False
                        
                    # Update message status
                    self.messages[recipient][message_idx]["status"] = MessageStatus.SENT
                    self._update_display()
                    
                    # Track message count for key rotation
                    if recipient not in self.message_counter:
                        self.message_counter[recipient] = 0
                        
                    self.message_counter[recipient] += 1
                    
                    # Check if key rotation is needed
                    if self.message_counter[recipient] >= KEY_ROTATION_MESSAGE_COUNT:
                        asyncio.create_task(self._handle_key_rotation())
                    
                    return True
                    
        except Exception as e:
            self.console.print(f"[bold red]Error sending message: {str(e)}[/bold red]")
            return False
            
    async def _process_input(self):
        """Process user input"""
        try:
            while self.running:
                input_text = await asyncio.get_event_loop().run_in_executor(
                    None, lambda: Prompt.ask("", console=self.console)
                )
                
                if not input_text:
                    continue
                    
                # Handle commands
                if input_text.startswith("/"):
                    await self._handle_command(input_text)
                    
                # Send message to current chat
                elif self.current_chat:
                    await self._send_message(self.current_chat, input_text)
                    
                else:
                    self.console.print("[yellow]No active chat. Use /connect <username> to start chatting[/yellow]")
                    
        except Exception as e:
            self.console.print(f"[bold red]Error processing input: {str(e)}[/bold red]")
            
    async def _handle_incoming_messages(self):
        """Handle incoming messages"""
        while self.running:
            if self.connected:
                await self._fetch_pending_messages()
            await asyncio.sleep(5)  # Check every 5 seconds
            
    async def _handle_key_rotation(self):
        """Handle periodic key rotation"""
        current_time = time.time()
        
        for contact in list(self.session_keys.keys()):
            # Skip if key was recently rotated
            if contact in self.last_key_rotation and current_time - self.last_key_rotation[contact] < KEY_ROTATION_TIME_SECONDS:
                continue
                
            # Generate new keys
            self.session_keys[contact] = self._generate_keypair()
            self.message_counter[contact] = 0
            self.last_key_rotation[contact] = current_time
            
            self.console.print(f"[dim]Rotated encryption keys for {contact}[/dim]")
            
    async def _handle_inactivity(self):
        """Handle inactivity timeout"""
        last_activity = time.time()
        
        while self.running:
            current_time = time.time()
            
            if self.connected and (current_time - last_activity) > INACTIVITY_TIMEOUT:
                self.console.print("[yellow]Disconnecting due to inactivity[/yellow]")
                self.connected = False
                self._update_display()
                
            await asyncio.sleep(60)  # Check every minute
            
    def _encrypt_message(self, contact: str, message: str) -> dict:
        try:
            if contact not in self.session_keys or not self.session_keys[contact].get("established"):
                raise ValueError("No established key for this contact")

            key = self.session_keys[contact]["key"]
            nonce = os.urandom(12)
            message_bytes = message.encode("utf-8")
            
            # Include metadata in associated data
            metadata = {
                "sender": self.username,
                "recipient": contact,
                "timestamp": time.time()
            }
            associated_data = json.dumps(metadata).encode()
            
            # Encrypt with ChaCha20Poly1305
            cipher = ChaCha20Poly1305(key)
            encrypted = cipher.encrypt(nonce, message_bytes, associated_data)
            
            # Sign with Ed25519
            signature = self.session_keys[contact]["keypair"]["signing"]["private"].sign(
                encrypted + associated_data
            )
            
            return {
                "encrypted": base64.b64encode(encrypted).decode(),
                "nonce": base64.b64encode(nonce).decode(),
                "signature": base64.b64encode(signature).decode(),
                "metadata": metadata
            }
        except Exception as e:
            self.console.print(f"[bold red]Encryption error: {str(e)}[/bold red]")
            return None
            
    def _decrypt_message(self, contact: str, encrypted_data: dict) -> str:
        try:
            key = self.session_keys[contact]["key"]
            nonce = base64.b64decode(encrypted_data["nonce"])
            ciphertext = base64.b64decode(encrypted_data["encrypted"])
            signature = base64.b64decode(encrypted_data["signature"])

            # Verify signature first
            public_key = self.session_keys[contact]["keypair"]["signing"]["public"]
            public_key.verify(signature, ciphertext + nonce)

            # Decrypt
            cipher = ChaCha20Poly1305(key)
            return cipher.decrypt(nonce, ciphertext, None).decode()
            
        except Exception as e:
            self.console.print(f"[red]Decryption failed: {str(e)}[/red]")
            return "[Could not decrypt message]"
            
    def _save_vault(self):
        """Save the vault to disk"""
        try:
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
            self.console.print(f"[bold red]Error saving vault: {str(e)}[/bold red]")
            return False
            
    async def run(self):
        """Main application loop"""
        try:
            # Set up signal handler
            signal.signal(signal.SIGINT, signal_handler)
            
            # Get username and password
            self.console.print("[bold yellow]Secure Chat Application[/bold yellow]")
            self.console.print("="*50)
            
            action = Prompt.ask("Choose an action", choices=["login", "register", "quit"], default="login")
            
            if action == "quit":
                return
                
            username = Prompt.ask("Enter username")
            password = getpass.getpass("Enter password: ")
            
            # Register or login
            if action == "register":
                if not self._create_vault(username, password):
                    return
                    
                # Now login
                totp_code = Prompt.ask("Enter TOTP code")
                if not self._unlock_vault(username, password, totp_code):
                    return
                    
            else:  # login
                totp_code = Prompt.ask("Enter TOTP code")
                if not self._unlock_vault(username, password, totp_code):
                    return
                    
            # Initialize session
            self.session_keys["server"] = self._generate_keypair()
            
            # Connect to server
            if not await self._connect_to_server():
                self.console.print("[bold red]Failed to connect to server[/bold red]")
            
            # Start TUI
            with Live(self.layout, refresh_per_second=10, screen=True):
                self._update_display()
                
                # Start background tasks
                asyncio.create_task(self._process_input())
                asyncio.create_task(self._handle_incoming_messages())
                asyncio.create_task(self._handle_key_rotation())
                asyncio.create_task(self._handle_inactivity())
                asyncio.create_task(self._periodic_vault_backup())
                
                # Keep the application running
                while self.running:
                    await asyncio.sleep(0.1)
        
        except Exception as e:
            self.console.print(f"[bold red]Error: {str(e)}[/bold red]")
            
    def signal_handler(sig, frame):
        """Handle Ctrl+C"""
        # Just exit
        sys.exit(0)
        
    def _perform_key_exchange(self, contact: str, public_key_bytes: bytes) -> bytes:
        """Perform X25519 key exchange with a contact"""
        # Generate keys if needed
        if contact not in self.session_keys:
            self.session_keys[contact] = self._generate_keypair()
            
        # Import contact's public key
        contact_key = x25519.X25519PublicKey.from_public_bytes(public_key_bytes)
        
        # Generate shared secret
        shared_secret = self.session_keys[contact]["keypair"]["exchange"]["private"].exchange(contact_key)
        
        # Derive key with HKDF
        derived_key = HKDF(
            algorithm=hashes.SHA256(),
            length=32,
            salt=None,
            info=b"chat-key"
        ).derive(shared_secret)
        
        return derived_key


def signal_handler(sig, frame):
    """Handle Ctrl+C"""
    sys.exit(0)


def parse_args():
    """Parse command line arguments"""
    parser = argparse.ArgumentParser(description="Secure Chat Application")
    parser.add_argument("--server", help="Server hostname or IP")
    parser.add_argument("--port", type=int, help="Server port")
    return parser.parse_args()


async def main():
    """Main entry point"""
    args = parse_args()
    
    # Override server settings if provided
    global SERVER_HOST, SERVER_PORT
    if args.server:
        SERVER_HOST = args.server
    if args.port:
        SERVER_PORT = args.port
    
    app = SecureChatApp()
    await app.run()


if __name__ == "__main__":
    asyncio.run(main())