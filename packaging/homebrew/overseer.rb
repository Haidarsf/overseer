cask "overseer" do
  arch arm: "arm64", intel: "x64"

  version "VERSION"
  sha256 arm:   "SHA256_OSX_ARM64",
         intel: "SHA256_OSX_X64"

  url "https://github.com/Haidarsf/overseer/releases/download/v#{version}/overseer-osx-#{arch}.tar.gz"
  name "Overseer"
  desc "See and steer your Claude Code sessions"
  homepage "https://github.com/Haidarsf/overseer"

  binary "overseer-osx-#{arch}/Overseer", target: "overseer"

  zap trash: "~/.overseer"
end
