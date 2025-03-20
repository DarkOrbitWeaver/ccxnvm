#!/usr/bin/env python3
"""
Secure Chat Relay Server
Handles message relaying and user presence without access to message content
"""

import asyncio
import base64
import json
import logging
import os
import time
import pickle
from pathlib import Path
from datetime import datetime, timedelta
from typing import Dict, List, Optional, Set

import uvicorn
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.asymmetric import ed25519, x25519
from cryptography.hazmat.primitives.kdf.hkdf import HKDF
from fastapi import FastAPI, HTTPException, Request, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s - %(name)s - %(levelname)s - %(message)s",
)
logger = logging.getLogger("secure-chat-server")

# Initialize FastAPI app
app = FastAPI(title="Secure Chat Relay Server")

# Add CORS middleware
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],  # For development only - restrict in production
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

# Replace all storage dictionaries with this
STORAGE_FILE = "data.pkl"

def load_storage():
    try:
        return pickle.loads(Path(STORAGE_FILE).read_bytes())
    except:
        return {
            "active_users": {},
            "pending_messages": {},
            "user_challenges": {},
            "user_public_keys": {},
            "user_last_seen": {}
        }

def save_storage(data):
    Path(STORAGE_FILE).write_bytes(pickle.dumps(data))

# Initialize storage
storage = load_storage()
active_users = storage["active_users"]
pending_messages = storage["pending_messages"]
user_challenges = storage["user_challenges"]
user_public_keys = storage["user_public_keys"]
user_last_seen = storage["user_last_seen"]

def derive_shared_secret(private_key: x25519.X25519PrivateKey, 
                        peer_public_key: x25519.X25519PublicKey) -> bytes:
    """Server-side shared secret derivation"""
    shared_key = private_key.exchange(peer_public_key)
    return HKDF(
        algorithm=hashes.SHA256(),
        length=32,
        salt=None,
        info=b'secure-chat-key'
    ).derive(shared_key)

# Models
class User(BaseModel):
    username: str
    public_key: str  # Base64 encoded X25519 public key


class Message(BaseModel):
    sender: str
    recipient: str
    encrypted_content: str  # Base64 encoded encrypted message
    signature: str  # Base64 encoded message signature
    timestamp: float


class AuthChallenge(BaseModel):
    username: str
    challenge: str  # Base64 encoded random challenge


class AuthResponse(BaseModel):
    username: str
    signature: str  # Base64 encoded signature of the challenge


# Cleanup expired messages and users
async def cleanup_task():
    while True:
        try:
            current_time = time.time()
            
            # Clean up messages older than TTL
            for username in list(pending_messages.keys()):
                pending_messages[username] = [
                    msg for msg in pending_messages[username]
                    if current_time - msg["timestamp"] < 604800  # 7 days
                ]
                
                # Remove empty lists
                if not pending_messages[username]:
                    del pending_messages[username]
            
            # Clean up users not seen in 24 hours
            for username in list(user_last_seen.keys()):
                if current_time - user_last_seen[username] > 86400:  # 24 hours
                    if username in active_users:
                        del active_users[username]
                    if username in user_public_keys:
                        del user_public_keys[username]
                    if username in user_challenges:
                        del user_challenges[username]
                    del user_last_seen[username]
            
            # Log stats
            logger.info(f"Active users: {len(active_users)}, Pending messages: {sum(len(msgs) for msgs in pending_messages.values())}")
            
            # Add this at the end of the loop
            save_storage({
                "active_users": active_users,
                "pending_messages": pending_messages,
                "user_challenges": user_challenges,
                "user_public_keys": user_public_keys,
                "user_last_seen": user_last_seen
            })
            
            await asyncio.sleep(3600)  # Run hourly
        except Exception as e:
            logger.error(f"Error in cleanup task: {e}")
            await asyncio.sleep(3600)  # Try again after an hour


@app.on_event("startup")
async def startup_event():
    # Start background cleanup task
    asyncio.create_task(cleanup_task())


@app.get("/")
async def root():
    return {"message": "Secure Chat Relay Server", "status": "online"}


@app.get("/keys/{username}")
async def get_public_key(username: str):
    if username not in user_public_keys:
        raise HTTPException(status_code=404, detail="User not found")
    return {"public_key": user_public_keys[username]}


@app.post("/register")
async def register_user(user: User):
    """Register a new user or update existing user's public key"""
    username = user.username
    
    if not valid_username(username):
        raise HTTPException(status_code=400, detail="Invalid username")
    
    try:
        # Store public key
        user_public_keys[username] = user.public_key
        user_last_seen[username] = time.time()
        
        # Generate random challenge for authentication
        challenge = os.urandom(32)
        challenge_b64 = base64.b64encode(challenge).decode("utf-8")
        user_challenges[username] = challenge_b64
        
        return {"username": username, "challenge": challenge_b64}
    except Exception as e:
        logger.error(f"Error registering user {username}: {e}")
        raise HTTPException(status_code=500, detail="Server error")


@app.post("/authenticate")
async def authenticate_user(auth: AuthResponse):
    username = auth.username
    
    if username not in user_challenges or username not in user_public_keys:
        raise HTTPException(status_code=400, detail="User not registered or challenge expired")
    
    try:
        public_key = ed25519.Ed25519PublicKey.from_public_bytes(
            base64.b64decode(user_public_keys[username])
        )
        challenge = base64.b64decode(user_challenges[username])
        signature = base64.b64decode(auth.signature)
        
        public_key.verify(signature, challenge)
        
        user_last_seen[username] = time.time()
        messages = pending_messages.get(username, [])
        
        return {
            "authenticated": True,
            "pending_message_count": len(messages)
        }
    except Exception as e:
        logger.error(f"Auth failed for {username}: {str(e)}")
        raise HTTPException(status_code=401, detail="Authentication failed")


@app.post("/message")
async def send_message(message: Message):
    sender = message.sender
    recipient = message.recipient
    
    # Verify the sender is registered
    if sender not in user_public_keys:
        raise HTTPException(status_code=400, detail="Sender not registered")
    
    try:
        # Verify message signature
        public_key = ed25519.Ed25519PublicKey.from_public_bytes(
            base64.b64decode(user_public_keys[sender])
        )
        signed_data = base64.b64decode(message.encrypted_content) + str(message.timestamp).encode()
        signature = base64.b64decode(message.signature)
        public_key.verify(signature, signed_data)
    except Exception as e:
        logger.error(f"Invalid signature from {sender}: {str(e)}")
        raise HTTPException(status_code=403, detail="Invalid message signature")
    
    # Update last seen timestamp
    user_last_seen[sender] = time.time()
    
    # Create message object
    msg_obj = {
        "sender": sender,
        "encrypted_content": message.encrypted_content,
        "signature": message.signature,
        "timestamp": message.timestamp
    }
    
    # Store message for recipient
    if recipient not in pending_messages:
        pending_messages[recipient] = []
    
    pending_messages[recipient].append(msg_obj)
    
    # If recipient is active, notify them
    if recipient in active_users:
        try:
            # In a real implementation, we would send a WebSocket notification
            pass
        except Exception as e:
            logger.error(f"Failed to notify recipient {recipient}: {e}")
    
    return {"status": "delivered", "timestamp": time.time()}


@app.get("/messages/{username}")
async def get_messages(username: str):
    """Retrieve pending messages for a user"""
    if username not in user_public_keys:
        raise HTTPException(status_code=400, detail="User not registered")
    
    # Update last seen timestamp
    user_last_seen[username] = time.time()
    
    # Get pending messages
    messages = pending_messages.get(username, [])
    
    # Clear pending messages
    if username in pending_messages:
        del pending_messages[username]
    
    return {"messages": messages}


@app.get("/status/{username}")
async def get_user_status(username: str):
    """Check if a user is online"""
    if username not in user_public_keys:
        raise HTTPException(status_code=404, detail="User not found")
    
    is_online = username in active_users
    last_seen = user_last_seen.get(username, 0)
    
    return {
        "username": username,
        "online": is_online,
        "last_seen": last_seen
    }


# WebSocket connection manager
class ConnectionManager:
    def __init__(self):
        self.active_connections: Dict[str, WebSocket] = {}
    
    async def connect(self, username: str, websocket: WebSocket):
        await websocket.accept()
        self.active_connections[username] = websocket
        active_users[username] = {"websocket": True}
    
    def disconnect(self, username: str):
        if username in self.active_connections:
            del self.active_connections[username]
        if username in active_users:
            del active_users[username]
    
    async def send_message(self, username: str, message: dict):
        if username in self.active_connections:
            await self.active_connections[username].send_json(message)


manager = ConnectionManager()


@app.websocket("/ws/{username}")
async def websocket_endpoint(websocket: WebSocket, username: str):
    """WebSocket endpoint for real-time messaging"""
    if username not in user_public_keys:
        await websocket.close(code=1008, reason="User not registered")
        return
    
    await manager.connect(username, websocket)
    
    try:
        # Notify client of any pending messages
        if username in pending_messages and pending_messages[username]:
            await websocket.send_json({
                "type": "notification",
                "message": f"You have {len(pending_messages[username])} pending messages"
            })
        
        # Update last seen timestamp
        user_last_seen[username] = time.time()
        
        while True:
            # Receive and process messages
            data = await websocket.receive_text()
            try:
                msg_data = json.loads(data)
                
                if msg_data["type"] == "message":
                    # Process and relay message
                    recipient = msg_data["recipient"]
                    
                    if recipient in manager.active_connections:
                        # Recipient is online, send directly
                        await manager.send_message(recipient, {
                            "type": "message",
                            "sender": username,
                            "content": msg_data["content"],
                            "timestamp": time.time()
                        })
                    else:
                        # Store message for later delivery
                        if recipient not in pending_messages:
                            pending_messages[recipient] = []
                        
                        pending_messages[recipient].append({
                            "sender": username,
                            "encrypted_content": msg_data["content"],
                            "signature": msg_data.get("signature", ""),
                            "timestamp": time.time()
                        })
                
                elif msg_data["type"] == "heartbeat":
                    # Update last seen timestamp
                    user_last_seen[username] = time.time()
                    await websocket.send_json({"type": "heartbeat_ack"})
            
            except json.JSONDecodeError:
                logger.error(f"Invalid JSON from user {username}")
            except Exception as e:
                logger.error(f"Error processing message from {username}: {e}")
    
    except WebSocketDisconnect:
        manager.disconnect(username)
        logger.info(f"User {username} disconnected")


def valid_username(username: str) -> bool:
    """Validate username format"""
    if not username or len(username) < 3 or len(username) > 32:
        return False
    
    # Check for valid characters (alphanumeric plus some special chars)
    import re
    return bool(re.match(r'^[a-zA-Z0-9_\-\.]+$', username))


if __name__ == "__main__":
    # Get port from environment variable or use default
    port = int(os.environ.get("PORT", 8000))
    
    # Run the server
    uvicorn.run("server:app", host="0.0.0.0", port=8000)