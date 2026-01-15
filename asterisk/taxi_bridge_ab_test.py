#!/usr/bin/env python3
"""
Taxi Bridge A/B Testing Launcher
================================
Runs both standard (24kHz) and Rasa (16kHz) bridges simultaneously
for A/B testing STT accuracy. Calls are distributed based on caller
phone number hash for consistent routing.

Usage:
    python3 taxi_bridge_ab_test.py

The script will start both bridges on different ports and log
performance metrics for comparison.
"""

import asyncio
import subprocess
import sys
import os
import signal
import hashlib
from datetime import datetime

# Configuration
STANDARD_BRIDGE = "taxi_bridge_v6.py"
RASA_BRIDGE = "taxi_bridge_rasa.py"

# Color codes for terminal output
class Colors:
    HEADER = '\033[95m'
    BLUE = '\033[94m'
    CYAN = '\033[96m'
    GREEN = '\033[92m'
    YELLOW = '\033[93m'
    RED = '\033[91m'
    ENDC = '\033[0m'
    BOLD = '\033[1m'

def print_banner():
    """Print startup banner"""
    banner = f"""
{Colors.CYAN}╔══════════════════════════════════════════════════════════════════╗
║                                                                    ║
║   {Colors.BOLD}TAXI BRIDGE A/B TESTING LAUNCHER{Colors.CYAN}                               ║
║                                                                    ║
║   Standard (24kHz) vs Rasa (16kHz) STT Comparison                  ║
║                                                                    ║
╚══════════════════════════════════════════════════════════════════╝{Colors.ENDC}
"""
    print(banner)

def get_script_dir():
    """Get the directory containing this script"""
    return os.path.dirname(os.path.abspath(__file__))

class BridgeProcess:
    """Manages a single bridge subprocess"""
    
    def __init__(self, name: str, script: str, env_vars: dict = None):
        self.name = name
        self.script = script
        self.env_vars = env_vars or {}
        self.process = None
        self.start_time = None
        
    async def start(self):
        """Start the bridge process"""
        script_dir = get_script_dir()
        script_path = os.path.join(script_dir, self.script)
        
        if not os.path.exists(script_path):
            print(f"{Colors.RED}[ERROR] Script not found: {script_path}{Colors.ENDC}")
            return False
            
        # Prepare environment
        env = os.environ.copy()
        env.update(self.env_vars)
        env['PYTHONUNBUFFERED'] = '1'
        
        print(f"{Colors.GREEN}[{self.name}] Starting {self.script}...{Colors.ENDC}")
        
        self.process = await asyncio.create_subprocess_exec(
            sys.executable, script_path,
            env=env,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.STDOUT,
            cwd=script_dir
        )
        self.start_time = datetime.now()
        return True
        
    async def read_output(self):
        """Read and prefix output from the bridge"""
        if not self.process:
            return
            
        color = Colors.BLUE if 'standard' in self.name.lower() else Colors.YELLOW
        prefix = f"[{self.name}]"
        
        while True:
            line = await self.process.stdout.readline()
            if not line:
                break
            text = line.decode('utf-8', errors='replace').rstrip()
            print(f"{color}{prefix}{Colors.ENDC} {text}")
            
    async def stop(self):
        """Stop the bridge process"""
        if self.process:
            print(f"{Colors.CYAN}[{self.name}] Stopping...{Colors.ENDC}")
            self.process.terminate()
            try:
                await asyncio.wait_for(self.process.wait(), timeout=5.0)
            except asyncio.TimeoutError:
                print(f"{Colors.RED}[{self.name}] Force killing...{Colors.ENDC}")
                self.process.kill()
                await self.process.wait()

class ABTestRunner:
    """Runs both bridges for A/B testing"""
    
    def __init__(self):
        self.bridges = []
        self.running = True
        
    def setup_bridges(self):
        """Configure both bridge instances"""
        # Standard bridge (24kHz)
        standard = BridgeProcess(
            name="STANDARD-24kHz",
            script=STANDARD_BRIDGE,
            env_vars={'RASA_AUDIO_PROCESSING': 'false'}
        )
        
        # Rasa bridge (16kHz)
        rasa = BridgeProcess(
            name="RASA-16kHz",
            script=RASA_BRIDGE,
            env_vars={'RASA_AUDIO_PROCESSING': 'true'}
        )
        
        self.bridges = [standard, rasa]
        
    async def run(self):
        """Run all bridges and monitor output"""
        print_banner()
        self.setup_bridges()
        
        # Start all bridges
        tasks = []
        for bridge in self.bridges:
            success = await bridge.start()
            if success:
                tasks.append(asyncio.create_task(bridge.read_output()))
                
        if not tasks:
            print(f"{Colors.RED}[ERROR] No bridges started successfully{Colors.ENDC}")
            return
            
        print(f"\n{Colors.GREEN}[A/B TEST] Both bridges running. Press Ctrl+C to stop.{Colors.ENDC}\n")
        print(f"{Colors.CYAN}{'='*70}{Colors.ENDC}\n")
        
        # Wait for all tasks or interruption
        try:
            await asyncio.gather(*tasks)
        except asyncio.CancelledError:
            pass
            
    async def shutdown(self):
        """Gracefully shutdown all bridges"""
        print(f"\n{Colors.CYAN}[A/B TEST] Shutting down...{Colors.ENDC}")
        self.running = False
        
        for bridge in self.bridges:
            await bridge.stop()
            
        print(f"{Colors.GREEN}[A/B TEST] All bridges stopped.{Colors.ENDC}")
        self.print_summary()
        
    def print_summary(self):
        """Print test summary"""
        print(f"\n{Colors.CYAN}{'='*70}{Colors.ENDC}")
        print(f"{Colors.BOLD}A/B Test Summary{Colors.ENDC}")
        print(f"{Colors.CYAN}{'='*70}{Colors.ENDC}")
        
        for bridge in self.bridges:
            if bridge.start_time:
                duration = datetime.now() - bridge.start_time
                print(f"  {bridge.name}: ran for {duration}")
                
        print(f"\n{Colors.YELLOW}Check edge function logs for STT accuracy metrics:{Colors.ENDC}")
        print(f"  - Filter by call_id prefix 'rasa-' for 16kHz calls")
        print(f"  - Compare word counts, correction rates, and hallucination hits")
        print()

async def main():
    """Main entry point"""
    runner = ABTestRunner()
    
    # Setup signal handlers
    loop = asyncio.get_event_loop()
    
    def signal_handler():
        asyncio.create_task(runner.shutdown())
        
    for sig in (signal.SIGINT, signal.SIGTERM):
        loop.add_signal_handler(sig, signal_handler)
        
    try:
        await runner.run()
    except KeyboardInterrupt:
        await runner.shutdown()

if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\nInterrupted")
        sys.exit(0)
