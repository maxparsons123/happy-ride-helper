#!/usr/bin/env python3
"""
Generate bridge-config.json from .env file.

Run this after remixing a project to auto-configure the bridge.

Usage:
    python3 scripts/generate-bridge-config.py
    
Or from project root:
    python3 scripts/generate-bridge-config.py --env .env --output bridge-config.json
"""

import os
import json
import argparse
from pathlib import Path


def parse_env_file(env_path: str) -> dict:
    """Parse .env file and return key-value pairs."""
    env_vars = {}
    
    if not os.path.exists(env_path):
        print(f"⚠️  .env file not found at {env_path}")
        return env_vars
    
    with open(env_path, 'r') as f:
        for line in f:
            line = line.strip()
            # Skip comments and empty lines
            if not line or line.startswith('#'):
                continue
            # Parse KEY=VALUE
            if '=' in line:
                key, value = line.split('=', 1)
                # Remove quotes if present
                value = value.strip('"').strip("'")
                env_vars[key.strip()] = value
    
    return env_vars


def generate_config(env_vars: dict) -> dict:
    """Generate bridge config from environment variables."""
    
    # Get values from env
    project_id = env_vars.get('VITE_SUPABASE_PROJECT_ID', '')
    supabase_url = env_vars.get('VITE_SUPABASE_URL', '')
    anon_key = env_vars.get('VITE_SUPABASE_PUBLISHABLE_KEY', '') or env_vars.get('VITE_SUPABASE_ANON_KEY', '')
    
    # Extract project_id from URL if not directly available
    if not project_id and supabase_url:
        # URL format: https://PROJECT_ID.supabase.co
        try:
            project_id = supabase_url.split('//')[1].split('.')[0]
        except:
            pass
    
    # Build URL if not provided
    if not supabase_url and project_id:
        supabase_url = f"https://{project_id}.supabase.co"
    
    if not project_id:
        print("❌ Could not determine project_id from .env file")
        print("   Expected: VITE_SUPABASE_PROJECT_ID or VITE_SUPABASE_URL")
        return None
    
    config = {
        "supabase": {
            "project_id": project_id,
            "url": supabase_url,
            "anon_key": anon_key
        },
        "edge_functions": {
            "taxi_realtime_ws": f"wss://{project_id}.supabase.co/functions/v1/taxi-realtime",
            "taxi_realtime_simple_ws": f"wss://{project_id}.supabase.co/functions/v1/taxi-realtime-simple",
            "taxi_passthrough_ws": f"wss://{project_id}.supabase.co/functions/v1/taxi-passthrough-ws",
            "taxi_webhook_test": f"https://{project_id}.supabase.co/functions/v1/taxi-webhook-test"
        },
        "_instructions": "Auto-generated from .env file. Run 'python3 scripts/generate-bridge-config.py' to regenerate."
    }
    
    return config


def main():
    parser = argparse.ArgumentParser(description='Generate bridge-config.json from .env file')
    parser.add_argument('--env', default='.env', help='Path to .env file (default: .env)')
    parser.add_argument('--output', default='bridge-config.json', help='Output path (default: bridge-config.json)')
    args = parser.parse_args()
    
    # Find project root (look for .env file)
    script_dir = Path(__file__).parent.absolute()
    project_root = script_dir.parent
    
    env_path = project_root / args.env
    output_path = project_root / args.output
    
    print(f"📂 Project root: {project_root}")
    print(f"📄 Reading: {env_path}")
    
    # Parse .env
    env_vars = parse_env_file(str(env_path))
    
    if not env_vars:
        print("❌ No environment variables found")
        return 1
    
    print(f"✅ Found {len(env_vars)} environment variables")
    
    # Generate config
    config = generate_config(env_vars)
    
    if not config:
        return 1
    
    # Write config
    with open(output_path, 'w') as f:
        json.dump(config, f, indent=2)
    
    print(f"✅ Generated: {output_path}")
    print(f"   Project ID: {config['supabase']['project_id']}")
    print(f"   WebSocket: {config['edge_functions']['taxi_realtime_simple_ws']}")
    
    return 0


if __name__ == "__main__":
    exit(main())
