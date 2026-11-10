# Template rendered by .github/workflows/release.yml ({{...}} placeholders).
# Copy the rendered whyso.rb from the release assets into the tap repo
# (homebrew-whyso/Formula/whyso.rb) so users can run: brew install <org>/whyso/whyso
class Whyso < Formula
  desc "Local provenance for AI-assisted coding: why every change was made"
  homepage "https://github.com/{{REPO}}"
  version "{{VERSION}}"
  license "Apache-2.0"

  on_macos do
    on_arm do
      url "{{BASE_URL}}/whyso-{{VERSION}}-osx-arm64.tar.gz"
      sha256 "{{SHA_OSX_ARM64}}"
    end
    on_intel do
      url "{{BASE_URL}}/whyso-{{VERSION}}-osx-x64.tar.gz"
      sha256 "{{SHA_OSX_X64}}"
    end
  end

  on_linux do
    on_intel do
      url "{{BASE_URL}}/whyso-{{VERSION}}-linux-x64.tar.gz"
      sha256 "{{SHA_LINUX_X64}}"
    end
  end

  def install
    bin.install "whyso"
  end

  test do
    assert_match "provenance", shell_output("#{bin}/whyso --help")
  end
end
