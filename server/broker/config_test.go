package main

import "testing"

func TestParseProxyNodes(t *testing.T) {
	nodes, err := parseProxyNodes("us-1=proxy-us-1.example:1080, us-2=198.51.100.20:2080")
	if err != nil {
		t.Fatalf("parse: %v", err)
	}
	if len(nodes) != 2 {
		t.Fatalf("got %d nodes, want 2", len(nodes))
	}
	if nodes[0].Name != "us-1" || nodes[0].Host != "proxy-us-1.example" || nodes[0].Port != 1080 {
		t.Fatalf("unexpected first node: %+v", nodes[0])
	}
	if nodes[1].Name != "us-2" || nodes[1].Host != "198.51.100.20" || nodes[1].Port != 2080 {
		t.Fatalf("unexpected second node: %+v", nodes[1])
	}
}

func TestParseProxyNodesRejectsUnsafeOrAmbiguousInput(t *testing.T) {
	cases := []string{
		"",
		"missing-name.example:1080",
		"bad name=proxy.example:1080",
		"us-1=proxy.example",
		"us-1=proxy.example:0",
		"us-1=proxy.example:1080,us-1=other.example:1080",
		"us-1=proxy.example:1080,us-2=proxy.example:1080",
		"us-1=[2001:db8::1]:1080",
	}
	for _, value := range cases {
		if _, err := parseProxyNodes(value); err == nil {
			t.Errorf("parseProxyNodes(%q) succeeded, want error", value)
		}
	}
}

func TestProxyNodesFromEnvironmentPrefersThePool(t *testing.T) {
	t.Setenv("BROKER_PROXY_NODES", "us-1=proxy-us-1.example:1080,us-2=proxy-us-2.example:1080")
	t.Setenv("BROKER_PROXY_HOST", "198.51.100.99")
	t.Setenv("BROKER_PROXY_PORT", "9999")

	nodes, err := proxyNodesFromEnvironment()
	if err != nil {
		t.Fatalf("environment: %v", err)
	}
	if len(nodes) != 2 || nodes[0].Name != "us-1" || nodes[1].Name != "us-2" {
		t.Fatalf("pool was not preferred: %+v", nodes)
	}
}

func TestProxyNodesFromEnvironmentKeepsSingleNodeCompatibility(t *testing.T) {
	t.Setenv("BROKER_PROXY_NODES", "")
	t.Setenv("BROKER_PROXY_HOST", "198.51.100.99")
	t.Setenv("BROKER_PROXY_PORT", "2080")

	nodes, err := proxyNodesFromEnvironment()
	if err != nil {
		t.Fatalf("legacy environment: %v", err)
	}
	if len(nodes) != 1 || nodes[0].Name != "socks5" ||
		nodes[0].Host != "198.51.100.99" || nodes[0].Port != 2080 {
		t.Fatalf("unexpected compatibility node: %+v", nodes)
	}
}
