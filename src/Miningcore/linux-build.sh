#!/bin/bash
echo ""
echo "The following dev-dependencies will be installed"
echo "Ubuntu: apt-get install git cmake build-essential libssl-dev pkg-config libboost-all-dev libsodium-dev libzmq5"
echo ""
sudo apt-get install git cmake build-essential libssl-dev pkg-config libboost-all-dev libsodium-dev libzmq5

echo ""
echo "Installing dotnet SDK core 3.1"
sudo apt-get update; \
  sudo apt-get install -y apt-transport-https && \
  sudo apt-get update && \
  sudo apt-get install -y dotnet-sdk-3.1

echo ""
echo "Building Miningcore 2.0 (.NET core 3.1)"
BUILDIR=${1:-../../build}
echo "Build folder: $BUILDIR"
dotnet publish -c Release --framework netcoreapp3.1 -o $BUILDIR
