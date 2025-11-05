#!/bin/bash

# HoverTrailer Plugin Deployment Script for Linux/macOS
# This script builds and deploys the plugin to a remote Jellyfin server

# Load configuration from VS Code settings and .env file
if [ -f .vscode/.env ]; then
    set -o allexport
    source .vscode/.env
    set +o allexport
fi

PLUGIN_NAME="Fovty.Plugin.HoverTrailer"
REMOTE_HOST="${JELLYFIN_REMOTE_HOST}"
REMOTE_USER="${JELLYFIN_REMOTE_USER}"
REMOTE_PATH="${JELLYFIN_REMOTE_PATH}"
BUILD_CONFIG="Debug"
TARGET_FRAMEWORK="net9.0"
BUILD_OUTPUT="./Fovty.Plugin.HoverTrailer/bin/Debug/net9.0/publish"
PROJECT_FILE="./Fovty.Plugin.HoverTrailer.sln"

# Check for JELLYFIN_REMOTE_PASSWORD environment variable
if [ -z "$JELLYFIN_REMOTE_PASSWORD" ]; then
    echo -e "${YELLOW}JELLYFIN_REMOTE_PASSWORD environment variable not set in .vscode/.env.${NC}"
    read -sp "Enter password for ${REMOTE_USER}@${REMOTE_HOST}: " JELLYFIN_REMOTE_PASSWORD
    echo
fi

# Check for sshpass
if ! command -v sshpass &> /dev/null; then
    echo -e "${RED}sshpass could not be found. Please install it to continue.${NC}"
    echo "On Debian/Ubuntu: sudo apt-get install sshpass"
    echo "On Fedora/CentOS: sudo dnf install sshpass"
    echo "On macOS (with Homebrew): brew install hudochenkov/sshpass/sshpass"
    exit 1
fi

export SSHPASS="$JELLYFIN_REMOTE_PASSWORD"
SSH_COMMAND="sshpass -e ssh -o StrictHostKeyChecking=no"
SCP_COMMAND="sshpass -e scp -o StrictHostKeyChecking=no"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Functions for each step
clean_build() {
    echo -e "${YELLOW}Cleaning previous build...${NC}"

    # Clean using dotnet clean
    export PATH="/home/user/.dotnet:$PATH"
    dotnet clean "$PROJECT_FILE" -c "$BUILD_CONFIG"
    if [ $? -ne 0 ]; then echo -e "${RED}❌ Dotnet clean failed${NC}"; exit 1; fi

    # Also manually remove bin and obj directories to ensure complete cleanup
    echo -e "${YELLOW}Removing bin and obj directories...${NC}"
    find . -name "bin" -type d -exec rm -rf {} + 2>/dev/null || true
    find . -name "obj" -type d -exec rm -rf {} + 2>/dev/null || true

    echo -e "${GREEN}✅ Clean completed (bin and obj directories removed)${NC}"
}

build_plugin() {
    echo -e "${YELLOW}Building plugin...${NC}"
    export PATH="/home/user/.dotnet:$PATH"
    dotnet publish "$PROJECT_FILE" -c "$BUILD_CONFIG" -f "$TARGET_FRAMEWORK" \
        --property:TreatWarningsAsErrors=false \
        /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary
    if [ $? -ne 0 ]; then echo -e "${RED}❌ Build failed${NC}"; exit 1; fi
    echo -e "${GREEN}✅ Build completed${NC}"
}

deploy_plugin() {
    echo -e "${YELLOW}Deploying to remote server...${NC}"
    echo "To: ${REMOTE_USER}@${REMOTE_HOST}:${REMOTE_PATH}"
    $SSH_COMMAND "${REMOTE_USER}@${REMOTE_HOST}" "mkdir -p '${REMOTE_PATH}'"
    if [ $? -ne 0 ]; then echo -e "${RED}❌ Failed to create remote directory${NC}"; exit 1; fi
    $SCP_COMMAND "${BUILD_OUTPUT}/${PLUGIN_NAME}.dll" "${REMOTE_USER}@${REMOTE_HOST}:${REMOTE_PATH}/"
    if [ $? -ne 0 ]; then echo -e "${RED}❌ Deployment failed${NC}"; exit 1; fi
    echo -e "${GREEN}✅ Deployment completed${NC}"
}

restart_jellyfin() {
    echo -e "${YELLOW}Restarting Jellyfin service...${NC}"
    $SSH_COMMAND "${REMOTE_USER}@${REMOTE_HOST}" "systemctl restart jellyfin"
    if [ $? -eq 0 ]; then
        echo -e "${GREEN}✅ Jellyfin service restarted${NC}"
    else
        echo -e "${YELLOW}⚠️ Failed to restart Jellyfin service${NC}"
    fi
}

# Main script logic
echo -e "${BLUE}=== HoverTrailer Plugin Deployment ===${NC}"
for arg in "$@"; do
    case $arg in
        clean)
            clean_build
            ;;
        build)
            build_plugin
            ;;
        deploy)
            deploy_plugin
            ;;
        restart)
            restart_jellyfin
            ;;
        *)
            echo -e "${RED}Unknown argument: $arg${NC}"
            exit 1
            ;;
    esac
done
echo -e "${GREEN}🎉 Script finished.${NC}"